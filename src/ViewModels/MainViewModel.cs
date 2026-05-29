using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using blink_o_mat.Infrastructure;
using blink_o_mat.Models;
using blink_o_mat.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;

namespace blink_o_mat.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int StretchRefreshDebounceMs = 180;
    private const int PreviewInteractiveMaxLongSide = 1600;
    // While the user is actively dragging an STF slider we render at this reduced
    // long-side so each re-stretch stays inside one display frame budget. Once the
    // user releases the slider we re-render at PreviewInteractiveMaxLongSide and then
    // upgrade to the full-resolution cached image.
    private const int PreviewInteractiveScrubbingMaxLongSide = 900;
    private const int PreviewFullResolutionIdleMs = 220;
    private const int MinimumPreviewCacheAhead = 8;
    private const int MinimumPreviewCacheBehind = 2;
    private const int MaximumPreviewCacheAhead = 32;
    private const int MaximumPreviewCacheBehind = 12;
    private const long PreviewCacheReservedBytes = 1024L * 1024 * 1024;
    private const long FrameLoadReservedBytes = 1024L * 1024 * 1024;
    private static readonly IReadOnlyList<SortFieldOption> DefaultSortFieldOptions =
    [
        new(FrameSortField.Score, "Score"),
        new(FrameSortField.ObservationDate, "Observation date"),
        new(FrameSortField.Fwhm, "FWHM"),
        new(FrameSortField.FwhmArcsec, "FWHM arcsec"),
        new(FrameSortField.Sqm, "SQM"),
        new(FrameSortField.SkyTemp, "Sky temp"),
        new(FrameSortField.Hfr, "HFR"),
        new(FrameSortField.StarCount, "Star count"),
        new(FrameSortField.Eccentricity, "Eccentricity"),
        new(FrameSortField.MeanBackground, "Mean background"),
        new(FrameSortField.Median, "Median"),
        new(FrameSortField.Mad, "MAD"),
        new(FrameSortField.Min, "Min"),
        new(FrameSortField.MinCount, "Min count"),
        new(FrameSortField.Max, "Max"),
        new(FrameSortField.MaxCount, "Max count")
    ];
    private static readonly IReadOnlyList<SortDirectionOption> DefaultSortDirectionOptions =
    [
        new(ListSortDirection.Ascending, "Ascending"),
        new(ListSortDirection.Descending, "Descending")
    ];

    private static SortFieldOption DefaultPrimarySortField =>
        DefaultSortFieldOptions.First(option => option.Value == FrameSortField.ObservationDate);

    private readonly record struct SortRuleSnapshot(FrameSortField Field, ListSortDirection Direction);

    private sealed class FrameItemComparer(IReadOnlyList<SortRuleSnapshot> rules) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is not FrameItem left)
            {
                return -1;
            }

            if (y is not FrameItem right)
            {
                return 1;
            }

            foreach (var rule in rules)
            {
                var comparison = CompareByField(left, right, rule.Field, rule.Direction);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
        }

        private static int CompareByField(FrameItem left, FrameItem right, FrameSortField field, ListSortDirection direction)
        {
            return field switch
            {
                FrameSortField.Score => CompareValues(left.OverallScore, right.OverallScore, direction),
                FrameSortField.ObservationDate => CompareNullableValues(left.ExposureDateTime, right.ExposureDateTime, direction),
                FrameSortField.Fwhm => CompareValues(left.Metrics.Fwhm, right.Metrics.Fwhm, direction),
                FrameSortField.FwhmArcsec => CompareNullableValues(left.Metrics.FwhmArcsec, right.Metrics.FwhmArcsec, direction),
                FrameSortField.Sqm => CompareNullableValues(left.Metrics.Sqm, right.Metrics.Sqm, direction),
                FrameSortField.SkyTemp => CompareNullableValues(left.Metrics.SkyTemp, right.Metrics.SkyTemp, direction),
                FrameSortField.Hfr => CompareValues(left.Metrics.Hfr, right.Metrics.Hfr, direction),
                FrameSortField.StarCount => CompareValues(left.Metrics.StarCount, right.Metrics.StarCount, direction),
                FrameSortField.Eccentricity => CompareValues(left.Metrics.Eccentricity, right.Metrics.Eccentricity, direction),
                FrameSortField.MeanBackground => CompareValues(left.Metrics.MeanBackground, right.Metrics.MeanBackground, direction),
                FrameSortField.Median => CompareValues(left.Metrics.Median, right.Metrics.Median, direction),
                FrameSortField.Mad => CompareValues(left.Metrics.Mad, right.Metrics.Mad, direction),
                FrameSortField.Min => CompareValues(left.Metrics.Min, right.Metrics.Min, direction),
                FrameSortField.MinCount => CompareValues(left.Metrics.MinCount, right.Metrics.MinCount, direction),
                FrameSortField.Max => CompareValues(left.Metrics.Max, right.Metrics.Max, direction),
                FrameSortField.MaxCount => CompareValues(left.Metrics.MaxCount, right.Metrics.MaxCount, direction),
                _ => 0
            };
        }

        private static int CompareValues<T>(T left, T right, ListSortDirection direction)
            where T : IComparable<T>
        {
            var comparison = left.CompareTo(right);
            return direction == ListSortDirection.Descending ? -comparison : comparison;
        }

        private static int CompareNullableValues<T>(T? left, T? right, ListSortDirection direction)
            where T : struct, IComparable<T>
        {
            if (!left.HasValue && !right.HasValue)
            {
                return 0;
            }

            if (!left.HasValue)
            {
                return 1;
            }

            if (!right.HasValue)
            {
                return -1;
            }

            var comparison = left.Value.CompareTo(right.Value);
            return direction == ListSortDirection.Descending ? -comparison : comparison;
        }
    }

    private sealed record LoadedFrameContext(
        FrameItem Item,
        string FilePath,
        int Width,
        int Height,
        double NormalizationMax,
        bool Rotate180,
        int ShiftX,
        int ShiftY,
        double? FocalLengthMm,
        double? PixelSizeUm,
        DateTimeOffset? ExposureDateTime,
        double? ExposureSeconds,
        string? FilterName,
        double? Sqm,
        double? SkyTemp,
        BitmapSource? FullImage);

    private readonly FrameDiscoveryService _discovery = new();
    private readonly RustafitsService _rustafits = new();
    private readonly FrameRejectionService _rejection = new();
    private readonly FrameMoveService _move = new();
    private readonly AppSettingsService _settings = new();
    private readonly SessionService _session = new();
    private readonly UpdateCheckService _updateCheck = new();

    public UpdateBannerViewModel UpdateBanner { get; } = new();
    public PerformanceIndicatorViewModel Performance { get; } = new();

    private string? _inputFolder;
    private string? _rejectedFolder;
    private bool _includeSubfolders;
    private string _status = "Ready";
    private bool _isBusy;
    private double _progressValue;
    private int _progressMaximum = 1;
    private bool _isProgressVisible;
    private bool _rejectSatelliteTrail = true;
    private double _stfShadows;
    private double _stfMidtones = 0.5;
    private double _stfHighlights = 1.0;
    private double _stfTargetBackground = 0.15;
    private double? _sessionFocalLengthMm;
    private double? _sessionPixelSizeUm;
    private int _approvedFrameCount;
    private int _eccentricityRejectedFrameCount;
    private int _fwhmRejectedFrameCount;
    private int _fwhmArcsecRejectedFrameCount;
    private int _hfrRejectedFrameCount;
    private int _meanBackgroundRejectedFrameCount;
    private int _rejectedFrameCount;
    private int _satelliteTrailRejectedFrameCount;
    private int _sqmRejectedFrameCount;
    private int _skyTempRejectedFrameCount;
    private int _starCountRejectedFrameCount;
    private int _scoreRejectedFrameCount;
    private bool _isSettingsOverlayOpen;
    private bool _showTrailSlider = true;
    private bool _showFwhmSlider = true;
    private bool _showFwhmArcsecSlider = true;
    private bool _showSqmSlider = true;
    private bool _showSkyTempSlider = true;
    private bool _showHfrSlider = true;
    private bool _showEccentricitySlider = true;
    private bool _showMeanBackgroundSlider = true;
    private bool _showStarsSlider = true;
    private bool _showScoreSlider = true;
    private bool _showTrailMetric = true;
    private bool _showFwhmMetric = true;
    private bool _showFwhmArcsecMetric = true;
    private bool _showSqmMetric = true;
    private bool _showSkyTempMetric = true;
    private bool _showHfrMetric = true;
    private bool _showEccentricityMetric = true;
    private bool _showMeanBackgroundMetric = true;
    private bool _showStarsMetric = true;
    private bool _showScoreMetric = true;
    private bool _useScoreFwhm = true;
    private bool _useScoreEccentricity = true;
    private bool _useScoreTrail = true;
    private bool _useScoreHfr = true;
    private bool _useScoreStars = true;
    private bool _useScoreMeanBackground = true;
    private double _scoreWeightFwhm = 3.0;
    private double _scoreWeightEccentricity = 2.5;
    private double _scoreWeightTrail = 2.0;
    private double _scoreWeightHfr = 1.5;
    private double _scoreWeightStars = 1.5;
    private double _scoreWeightMeanBackground = 0.5;

    private string _newProfileName = string.Empty;
    private string _defaultProfileName = "Default";
    private SettingsProfile? _selectedSettingsProfile;
    private bool _isApplyingProfile;
    private bool _isLoadingInitialSettings;
    private bool _isApplyingScoreWeightPreset;
    private bool _hasManualRoi;
    private bool _autoStretchPerFrame = true;
    private bool _skipRejectedInPreview;
    private bool _showAccepted = true;
    private bool _showRejected = true;
    private bool _isUpdatingFilterSelection;
    private FrameItem? _selectedFrame;
    private bool _watchFolderEnabled;
    private bool _isWatchingFolder;
    private FileSystemWatcher? _folderWatcher;
    private List<FileSystemWatcher>? _folderWatchers;
    private readonly SemaphoreSlim _watcherAddSemaphore = new(1, 1);
    private readonly HashSet<string> _watcherKnownFiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<LoadedFrameContext> _loadedFrames = [];
    private PreviewWindow? _previewWindow;
    private FramePreviewViewModel? _previewVm;
    private FrameItem? _previewItem;
    private (double Left, double Top, double Width, double Height)? _manualRoiRect;
    private CancellationTokenSource? _previewCacheCts;
    private CancellationTokenSource? _stretchRefreshCts;
    private readonly SemaphoreSlim _thumbnailRefreshSemaphore = new(1, 1);
    private readonly SemaphoreSlim _previewRefreshSemaphore = new(1, 1);
    private bool _isThumbnailRefreshRunning;
    private bool _thumbnailRefreshPendingWhilePreviewOpen;
    private bool _isInteractiveStretchActive;
    private bool _isAlignmentEnabled;
    // Cache of the materialized (decoded + oriented) raw pixel data for the currently
    // previewed frame. Held in memory so STF slider scrubbing can re-stretch without
    // touching disk or repeating the FITS decode. Cleared when the preview item changes.
    private FrameItem? _interactiveRawItem;
    private RustafitsService.LoadedFrame? _interactiveRawFrame;
    private string _totalIntegrationTimeText = string.Empty;
    private string _acceptedIntegrationTimeText = string.Empty;
    private double _overallAcceptedRatio;

    public RangeObservableCollection<FrameItem> Frames { get; } = [];
    public ICollectionView FilteredFrames { get; }
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];
    public ObservableCollection<FilterChipViewModel> RejectionScopeChips { get; } = [];
    public ObservableCollection<FilterChipGroupViewModel> RejectionScopeGroups { get; } = [];
    public ObservableCollection<FilterSummaryViewModel> FilterSummaries { get; } = [];

    // Per-filter thresholds. Key is the normalized filter name (empty string = "(no filter)").
    private readonly Dictionary<string, Thresholds> _filterThresholds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUpdatingRejectionScope;
    public ObservableCollection<FrameSortRuleViewModel> SortRules { get; } = [];
    public IReadOnlyList<SortFieldOption> SortFieldOptions => DefaultSortFieldOptions;
    public IReadOnlyList<SortDirectionOption> SortDirectionOptions => DefaultSortDirectionOptions;

    public string? InputFolder
    {
        get => _inputFolder;
        set
        {
            if (_inputFolder == value) return;
            _inputFolder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasInputFolder));
            SaveFolderSettings();
            ((RelayCommand)LoadFramesCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasInputFolder => !string.IsNullOrWhiteSpace(_inputFolder);

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value) return;
            _includeSubfolders = value;
            OnPropertyChanged();
            SaveFolderSettings();
            PersistProfileUiFlags();
        }
    }

    public bool WatchFolderEnabled
    {
        get => _watchFolderEnabled;
        set
        {
            if (_watchFolderEnabled == value) return;
            _watchFolderEnabled = value;
            OnPropertyChanged();
            SaveFolderSettings();
            PersistProfileUiFlags();
            if (!value)
                StopFolderWatch();
        }
    }

    public bool IsWatchingFolder
    {
        get => _isWatchingFolder;
        private set
        {
            if (_isWatchingFolder == value) return;
            _isWatchingFolder = value;
            OnPropertyChanged();
        }
    }

    public bool HasFilterChips => FilterChips.Count > 0;

    public bool HasMultipleFilterChips => FilterChips.Count > 1;

    public int TotalFrameCount => GetAllFilteredFrames().Count();

    public string TotalIntegrationTimeText
    {
        get => _totalIntegrationTimeText;
        private set { if (_totalIntegrationTimeText == value) return; _totalIntegrationTimeText = value; OnPropertyChanged(); }
    }

    public string AcceptedIntegrationTimeText
    {
        get => _acceptedIntegrationTimeText;
        private set { if (_acceptedIntegrationTimeText == value) return; _acceptedIntegrationTimeText = value; OnPropertyChanged(); }
    }

    public double OverallAcceptedRatio
    {
        get => _overallAcceptedRatio;
        private set { if (Math.Abs(_overallAcceptedRatio - value) < 0.001) return; _overallAcceptedRatio = value; OnPropertyChanged(); }
    }

    public bool ShowAccepted
    {
        get => _showAccepted;
        set
        {
            if (_showAccepted == value) return;
            _showAccepted = value;
            OnPropertyChanged();
            FilteredFrames.Refresh();
            UpdateFrameStatistics();
            RefreshPreviewVisibleFrames();
        }
    }

    public bool ShowRejected
    {
        get => _showRejected;
        set
        {
            if (_showRejected == value) return;
            _showRejected = value;
            OnPropertyChanged();
            FilteredFrames.Refresh();
            UpdateFrameStatistics();
            RefreshPreviewVisibleFrames();
        }
    }

    public int RejectedFrameCount
    {
        get => _rejectedFrameCount;
        private set
        {
            if (_rejectedFrameCount == value) return;
            _rejectedFrameCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RejectedFramePercentageText));
            OnPropertyChanged(nameof(MoveRejectedEnabled));
        }
    }

    public bool MoveRejectedEnabled => !IsBusy && RejectedFrameCount > 0 && !string.IsNullOrWhiteSpace(RejectedFolder);

    public int ApprovedFrameCount
    {
        get => _approvedFrameCount;
        private set
        {
            if (_approvedFrameCount == value) return;
            _approvedFrameCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApprovedFramePercentageText));
        }
    }

    public int SkyTempRejectedFrameCount
    {
        get => _skyTempRejectedFrameCount;
        private set
        {
            if (_skyTempRejectedFrameCount == value) return;
            _skyTempRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public string RejectedFramePercentageText => TotalFrameCount == 0
        ? "0.0%"
        : $"{(double)RejectedFrameCount / TotalFrameCount:P1}";

    public string ApprovedFramePercentageText => TotalFrameCount == 0
        ? "0.0%"
        : $"{(double)ApprovedFrameCount / TotalFrameCount:P1}";

    public int FwhmRejectedFrameCount
    {
        get => _fwhmRejectedFrameCount;
        private set
        {
            if (_fwhmRejectedFrameCount == value) return;
            _fwhmRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int FwhmArcsecRejectedFrameCount
    {
        get => _fwhmArcsecRejectedFrameCount;
        private set
        {
            if (_fwhmArcsecRejectedFrameCount == value) return;
            _fwhmArcsecRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int HfrRejectedFrameCount
    {
        get => _hfrRejectedFrameCount;
        private set
        {
            if (_hfrRejectedFrameCount == value) return;
            _hfrRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int SqmRejectedFrameCount
    {
        get => _sqmRejectedFrameCount;
        private set
        {
            if (_sqmRejectedFrameCount == value) return;
            _sqmRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int EccentricityRejectedFrameCount
    {
        get => _eccentricityRejectedFrameCount;
        private set
        {
            if (_eccentricityRejectedFrameCount == value) return;
            _eccentricityRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int MeanBackgroundRejectedFrameCount
    {
        get => _meanBackgroundRejectedFrameCount;
        private set
        {
            if (_meanBackgroundRejectedFrameCount == value) return;
            _meanBackgroundRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int StarCountRejectedFrameCount
    {
        get => _starCountRejectedFrameCount;
        private set
        {
            if (_starCountRejectedFrameCount == value) return;
            _starCountRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int ScoreRejectedFrameCount
    {
        get => _scoreRejectedFrameCount;
        private set
        {
            if (_scoreRejectedFrameCount == value) return;
            _scoreRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public int SatelliteTrailRejectedFrameCount
    {
        get => _satelliteTrailRejectedFrameCount;
        private set
        {
            if (_satelliteTrailRejectedFrameCount == value) return;
            _satelliteTrailRejectedFrameCount = value;
            OnPropertyChanged();
        }
    }

    public double StfShadows
    {
        get => _stfShadows;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_stfShadows - clamped) < 0.0001) return;
            _stfShadows = clamped;
            OnPropertyChanged();
            SwitchToManualStretch();
            OnStretchSettingsChanged();
        }
    }

    public double StfMidtones
    {
        get => _stfMidtones;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_stfMidtones - clamped) < 0.0001) return;
            _stfMidtones = clamped;
            OnPropertyChanged();
            SwitchToManualStretch();
            OnStretchSettingsChanged();
        }
    }

    public double StfHighlights
    {
        get => _stfHighlights;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_stfHighlights - clamped) < 0.0001) return;
            _stfHighlights = clamped;
            OnPropertyChanged();
            SwitchToManualStretch();
            OnStretchSettingsChanged();
        }
    }

    private void SwitchToManualStretch()
    {
        if (!_autoStretchPerFrame)
        {
            return;
        }

        // Adjusting Shadows / Midtones / Highlights is a manual override of the
        // per-frame auto-stretch. Without this switch, GetStfForFrame would call
        // ComputeAutoStretch on every render and discard the slider values, so the
        // sliders would appear to have no visible effect. The Target Background
        // slider stays in auto mode because it is an INPUT to the auto-stretch.
        _autoStretchPerFrame = false;
        OnPropertyChanged(nameof(AutoStretchPerFrame));
    }

    private StfParameters ActiveStf => new(_stfShadows, _stfMidtones, _stfHighlights);

    public double StfTargetBackground
    {
        get => _stfTargetBackground;
        set
        {
            var clamped = Math.Clamp(value, 0.01, 0.5);
            if (Math.Abs(_stfTargetBackground - clamped) < 0.001) return;
            _stfTargetBackground = clamped;
            OnPropertyChanged();
            // The target background affects per-frame auto-stretch results, so any cached
            // full-resolution images and existing thumbnails/ROI bitmaps must be regenerated.
            InvalidateFullImageCaches();
            OnStretchSettingsChanged();
            PersistProfileUiFlags();
        }
    }

    public bool SkipRejectedInPreview
    {
        get => _skipRejectedInPreview;
        set
        {
            if (_skipRejectedInPreview == value) return;
            _skipRejectedInPreview = value;
            OnPropertyChanged();
        }
    }

    public bool AutoStretchPerFrame
    {
        get => _autoStretchPerFrame;
        set
        {
            if (_autoStretchPerFrame == value) return;
            _autoStretchPerFrame = value;
            OnPropertyChanged();
            InvalidateFullImageCaches();
            OnStretchSettingsChanged();
        }
    }

    private StfParameters GetStfForFrame(RustafitsService.LoadedFrame frame) =>
        _autoStretchPerFrame ? _rustafits.ComputeAutoStretch(frame, _stfTargetBackground) : ActiveStf;

    public string? RejectedFolder
    {
        get => _rejectedFolder;
        set
        {
            if (_rejectedFolder == value) return;
            _rejectedFolder = value;
            OnPropertyChanged();
            SaveFolderSettings();
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(MoveRejectedEnabled));
        }
    }

    public FrameItem? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            if (ReferenceEquals(_selectedFrame, value)) return;
            _selectedFrame = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            ((RelayCommand)LoadFramesCommand).RaiseCanExecuteChanged();
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SaveSessionCommand).RaiseCanExecuteChanged();
            ((RelayCommand)LoadSessionCommand).RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(MoveRejectedEnabled));
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            if (Math.Abs(_progressValue - value) < double.Epsilon) return;
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        private set
        {
            if (_progressMaximum == value) return;
            _progressMaximum = value;
            OnPropertyChanged();
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set
        {
            if (_isProgressVisible == value) return;
            _isProgressVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsSettingsOverlayOpen
    {
        get => _isSettingsOverlayOpen;
        set
        {
            if (_isSettingsOverlayOpen == value) return;
            _isSettingsOverlayOpen = value;
            OnPropertyChanged();
        }
    }

    public bool ShowTrailSlider { get => _showTrailSlider; set { if (_showTrailSlider == value) return; _showTrailSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowFwhmSlider { get => _showFwhmSlider; set { if (_showFwhmSlider == value) return; _showFwhmSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowFwhmArcsecSlider { get => _showFwhmArcsecSlider; set { if (_showFwhmArcsecSlider == value) return; _showFwhmArcsecSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowSqmSlider { get => _showSqmSlider; set { if (_showSqmSlider == value) return; _showSqmSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowSkyTempSlider { get => _showSkyTempSlider; set { if (_showSkyTempSlider == value) return; _showSkyTempSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowHfrSlider { get => _showHfrSlider; set { if (_showHfrSlider == value) return; _showHfrSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowEccentricitySlider { get => _showEccentricitySlider; set { if (_showEccentricitySlider == value) return; _showEccentricitySlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowMeanBackgroundSlider { get => _showMeanBackgroundSlider; set { if (_showMeanBackgroundSlider == value) return; _showMeanBackgroundSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowStarsSlider { get => _showStarsSlider; set { if (_showStarsSlider == value) return; _showStarsSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowScoreSlider { get => _showScoreSlider; set { if (_showScoreSlider == value) return; _showScoreSlider = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }

    public bool ShowTrailMetric { get => _showTrailMetric; set { if (_showTrailMetric == value) return; _showTrailMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowFwhmMetric { get => _showFwhmMetric; set { if (_showFwhmMetric == value) return; _showFwhmMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowFwhmArcsecMetric { get => _showFwhmArcsecMetric; set { if (_showFwhmArcsecMetric == value) return; _showFwhmArcsecMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowSqmMetric { get => _showSqmMetric; set { if (_showSqmMetric == value) return; _showSqmMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowSkyTempMetric { get => _showSkyTempMetric; set { if (_showSkyTempMetric == value) return; _showSkyTempMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }
    public bool ShowHfrMetric { get => _showHfrMetric; set { if (_showHfrMetric == value) return; _showHfrMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowEccentricityMetric { get => _showEccentricityMetric; set { if (_showEccentricityMetric == value) return; _showEccentricityMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowMeanBackgroundMetric { get => _showMeanBackgroundMetric; set { if (_showMeanBackgroundMetric == value) return; _showMeanBackgroundMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowStarsMetric { get => _showStarsMetric; set { if (_showStarsMetric == value) return; _showStarsMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); OnScoreSettingsChanged(); } }
    public bool ShowScoreMetric { get => _showScoreMetric; set { if (_showScoreMetric == value) return; _showScoreMetric = value; OnPropertyChanged(); OnRejectionCriteriaVisibilityChanged(); } }

    public bool UseScoreTrail { get => _useScoreTrail; set { if (_useScoreTrail == value) return; _useScoreTrail = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }
    public bool UseScoreFwhm { get => _useScoreFwhm; set { if (_useScoreFwhm == value) return; _useScoreFwhm = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }
    public bool UseScoreHfr { get => _useScoreHfr; set { if (_useScoreHfr == value) return; _useScoreHfr = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }
    public bool UseScoreEccentricity { get => _useScoreEccentricity; set { if (_useScoreEccentricity == value) return; _useScoreEccentricity = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }
    public bool UseScoreStars { get => _useScoreStars; set { if (_useScoreStars == value) return; _useScoreStars = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }
    public bool UseScoreMeanBackground { get => _useScoreMeanBackground; set { if (_useScoreMeanBackground == value) return; _useScoreMeanBackground = value; OnPropertyChanged(); OnScoreSettingsChanged(); } }

    public double ScoreWeightFwhm
    {
        get => _scoreWeightFwhm;
        set
        {
            if (Math.Abs(_scoreWeightFwhm - value) < double.Epsilon) return;
            _scoreWeightFwhm = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public double ScoreWeightEccentricity
    {
        get => _scoreWeightEccentricity;
        set
        {
            if (Math.Abs(_scoreWeightEccentricity - value) < double.Epsilon) return;
            _scoreWeightEccentricity = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public double ScoreWeightTrail
    {
        get => _scoreWeightTrail;
        set
        {
            if (Math.Abs(_scoreWeightTrail - value) < double.Epsilon) return;
            _scoreWeightTrail = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public double ScoreWeightHfr
    {
        get => _scoreWeightHfr;
        set
        {
            if (Math.Abs(_scoreWeightHfr - value) < double.Epsilon) return;
            _scoreWeightHfr = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public double ScoreWeightStars
    {
        get => _scoreWeightStars;
        set
        {
            if (Math.Abs(_scoreWeightStars - value) < double.Epsilon) return;
            _scoreWeightStars = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public double ScoreWeightMeanBackground
    {
        get => _scoreWeightMeanBackground;
        set
        {
            if (Math.Abs(_scoreWeightMeanBackground - value) < double.Epsilon) return;
            _scoreWeightMeanBackground = value;
            OnPropertyChanged();
            if (!_isApplyingScoreWeightPreset)
            {
                OnScoreSettingsChanged();
            }
        }
    }

    public void ResetScoreWeightsToDefaults()
    {
        _isApplyingScoreWeightPreset = true;
        try
        {
            ScoreWeightFwhm = 3.0;
            ScoreWeightEccentricity = 2.5;
            ScoreWeightTrail = 2.0;
            ScoreWeightHfr = 1.5;
            ScoreWeightStars = 1.5;
            ScoreWeightMeanBackground = 0.5;
        }
        finally
        {
            _isApplyingScoreWeightPreset = false;
        }

        OnScoreSettingsChanged();
    }

    private void OnScoreSettingsChanged()
    {
        PersistProfileUiFlags();
        if (!_isApplyingProfile && !_isLoadingInitialSettings)
        {
            UpdateFrameComparisons();
            ApplyThresholds();
        }
    }

    public bool AutoCalcTrailThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcTrailThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcTrailThreshold = v, value, nameof(AutoCalcTrailThreshold));
    }

    public bool AutoCalcFwhmThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcFwhmThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcFwhmThreshold = v, value, nameof(AutoCalcFwhmThreshold));
    }

    public bool AutoCalcFwhmArcsecThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcFwhmArcsecThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcFwhmArcsecThreshold = v, value, nameof(AutoCalcFwhmArcsecThreshold));
    }

    public bool AutoCalcSqmThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcSqmThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcSqmThreshold = v, value, nameof(AutoCalcSqmThreshold));
    }

    public bool AutoCalcSkyTempThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcSkyTempThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcSkyTempThreshold = v, value, nameof(AutoCalcSkyTempThreshold));
    }

    public bool AutoCalcHfrThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcHfrThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcHfrThreshold = v, value, nameof(AutoCalcHfrThreshold));
    }

    public bool AutoCalcEccentricityThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcEccentricityThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcEccentricityThreshold = v, value, nameof(AutoCalcEccentricityThreshold));
    }

    public bool AutoCalcMeanBackgroundThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcMeanBackgroundThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcMeanBackgroundThreshold = v, value, nameof(AutoCalcMeanBackgroundThreshold));
    }

    public bool AutoCalcStarsThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcStarsThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcStarsThreshold = v, value, nameof(AutoCalcStarsThreshold));
    }

    public bool AutoCalcScoreThreshold
    {
        get => GetScopedThreshold(t => t.AutoCalcScoreThreshold);
        set => SetScopedThreshold((t, v) => t.AutoCalcScoreThreshold = v, value, nameof(AutoCalcScoreThreshold));
    }

    private Thresholds GetSettingsThresholds() => SelectedSettingsProfile?.Thresholds ?? GetThresholdsForKey(string.Empty);

    public bool SettingsAutoCalcTrailThreshold
    {
        get => GetSettingsThresholds().AutoCalcTrailThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcTrailThreshold == value) return;
            t.AutoCalcTrailThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcFwhmThreshold
    {
        get => GetSettingsThresholds().AutoCalcFwhmThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcFwhmThreshold == value) return;
            t.AutoCalcFwhmThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcFwhmArcsecThreshold
    {
        get => GetSettingsThresholds().AutoCalcFwhmArcsecThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcFwhmArcsecThreshold == value) return;
            t.AutoCalcFwhmArcsecThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcSqmThreshold
    {
        get => GetSettingsThresholds().AutoCalcSqmThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcSqmThreshold == value) return;
            t.AutoCalcSqmThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcSkyTempThreshold
    {
        get => GetSettingsThresholds().AutoCalcSkyTempThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcSkyTempThreshold == value) return;
            t.AutoCalcSkyTempThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcHfrThreshold
    {
        get => GetSettingsThresholds().AutoCalcHfrThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcHfrThreshold == value) return;
            t.AutoCalcHfrThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcEccentricityThreshold
    {
        get => GetSettingsThresholds().AutoCalcEccentricityThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcEccentricityThreshold == value) return;
            t.AutoCalcEccentricityThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcMeanBackgroundThreshold
    {
        get => GetSettingsThresholds().AutoCalcMeanBackgroundThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcMeanBackgroundThreshold == value) return;
            t.AutoCalcMeanBackgroundThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcStarsThreshold
    {
        get => GetSettingsThresholds().AutoCalcStarsThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcStarsThreshold == value) return;
            t.AutoCalcStarsThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public bool SettingsAutoCalcScoreThreshold
    {
        get => GetSettingsThresholds().AutoCalcScoreThreshold;
        set
        {
            var t = GetSettingsThresholds();
            if (t.AutoCalcScoreThreshold == value) return;
            t.AutoCalcScoreThreshold = value;
            OnPropertyChanged();
            PersistAppSettings();
        }
    }

    public double SettingsMaxFwhm
    {
        get => GetSettingsThresholds().MaxFwhm;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxFwhm - value) < double.Epsilon) return;
            t.MaxFwhm = value;
            t.AutoCalcFwhmThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcFwhmThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMaxFwhmArcsec
    {
        get => GetSettingsThresholds().MaxFwhmArcsec;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxFwhmArcsec - value) < double.Epsilon) return;
            t.MaxFwhmArcsec = value;
            t.AutoCalcFwhmArcsecThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcFwhmArcsecThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMinSqm
    {
        get => GetSettingsThresholds().MinSqm;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MinSqm - value) < double.Epsilon) return;
            t.MinSqm = value;
            t.AutoCalcSqmThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcSqmThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMaxSkyTemp
    {
        get => GetSettingsThresholds().MaxSkyTemp;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxSkyTemp - value) < double.Epsilon) return;
            t.MaxSkyTemp = value;
            t.AutoCalcSkyTempThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcSkyTempThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMaxHfr
    {
        get => GetSettingsThresholds().MaxHfr;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxHfr - value) < double.Epsilon) return;
            t.MaxHfr = value;
            t.AutoCalcHfrThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcHfrThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMaxEccentricity
    {
        get => GetSettingsThresholds().MaxEccentricity;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxEccentricity - value) < double.Epsilon) return;
            t.MaxEccentricity = value;
            t.AutoCalcEccentricityThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcEccentricityThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMaxMeanBackground
    {
        get => GetSettingsThresholds().MaxMeanBackground;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MaxMeanBackground - value) < double.Epsilon) return;
            t.MaxMeanBackground = value;
            t.AutoCalcMeanBackgroundThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcMeanBackgroundThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMinStars
    {
        get => GetSettingsThresholds().MinStars;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MinStars - value) < double.Epsilon) return;
            t.MinStars = value;
            t.AutoCalcStarsThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcStarsThreshold));
            PersistAppSettings();
        }
    }

    public double SettingsMinScore
    {
        get => GetSettingsThresholds().MinScore;
        set
        {
            var t = GetSettingsThresholds();
            if (Math.Abs(t.MinScore - value) < double.Epsilon) return;
            t.MinScore = value;
            t.AutoCalcScoreThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcScoreThreshold));
            PersistAppSettings();
        }
    }

    public int SettingsMinSatelliteConfidence
    {
        get => GetSettingsThresholds().MinSatelliteConfidence;
        set
        {
            var t = GetSettingsThresholds();
            if (t.MinSatelliteConfidence == value) return;
            t.MinSatelliteConfidence = value;
            t.AutoCalcTrailThreshold = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SettingsAutoCalcTrailThreshold));
            PersistAppSettings();
        }
    }

    private void OnRejectionCriteriaVisibilityChanged()
    {
        PersistProfileUiFlags();
        if (!_isApplyingProfile && !_isLoadingInitialSettings)
        {
            ApplyThresholds();
        }
    }

    public ObservableCollection<SettingsProfile> SettingsProfiles { get; } = [];

    public SettingsProfile? SelectedSettingsProfile
    {
        get => _selectedSettingsProfile;
        set
        {
            if (ReferenceEquals(_selectedSettingsProfile, value)) return;
            _selectedSettingsProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProfileName));
            OnPropertyChanged(nameof(IsSelectedProfileDefault));
            ((RelayCommand)AddSettingsProfileCommand).RaiseCanExecuteChanged();
            if (value is null || _isApplyingProfile) return;
            ApplySelectedProfileSettings(value);
        }
    }

    public string SelectedProfileName
    {
        get => SelectedSettingsProfile?.Name ?? string.Empty;
        set
        {
            var target = SettingsProfiles.FirstOrDefault(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                SelectedSettingsProfile = target;
            }
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (_newProfileName == value) return;
            _newProfileName = value;
            OnPropertyChanged();
            ((RelayCommand)AddSettingsProfileCommand).RaiseCanExecuteChanged();
        }
    }

    public string DefaultProfileName
    {
        get => _defaultProfileName;
        set
        {
            var normalized = SettingsProfile.NormalizeName(value);
            if (_defaultProfileName == normalized) return;
            _defaultProfileName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedProfileDefault));
            PersistAppSettings();
        }
    }

    public bool IsSelectedProfileDefault
    {
        get => SelectedSettingsProfile is not null && string.Equals(DefaultProfileName, SelectedSettingsProfile.Name, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value || SelectedSettingsProfile is null)
            {
                OnPropertyChanged();
                return;
            }

            DefaultProfileName = SelectedSettingsProfile.Name;
            OnPropertyChanged();
        }
    }

    public ICommand AddSettingsProfileCommand { get; }

    private bool CanAddSettingsProfile()
    {
        var normalized = SettingsProfile.NormalizeName(NewProfileName);
        return !SettingsProfiles.Any(p => string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void AddSettingsProfile()
    {
        var profile = CreateProfileFromCurrentState(SettingsProfile.NormalizeName(NewProfileName));
        SettingsProfiles.Add(profile);
        NewProfileName = profile.Name;
        SelectedSettingsProfile = profile;
        PersistAppSettings();
    }

    public bool TryCreateSettingsProfile(string? profileName)
    {
        var normalized = SettingsProfile.NormalizeName(profileName);
        if (SettingsProfiles.Any(p => string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        NewProfileName = normalized;
        AddSettingsProfile();
        return true;
    }

    private static Thresholds CloneThresholds(Thresholds thresholds)
    {
        return new Thresholds
        {
            MaxFwhm = thresholds.MaxFwhm,
            MaxFwhmArcsec = thresholds.MaxFwhmArcsec,
            MinSqm = thresholds.MinSqm,
            MaxSkyTemp = thresholds.MaxSkyTemp,
            MaxHfr = thresholds.MaxHfr,
            MaxEccentricity = thresholds.MaxEccentricity,
            MaxMeanBackground = thresholds.MaxMeanBackground,
            MinStars = thresholds.MinStars,
            MinSatelliteConfidence = thresholds.MinSatelliteConfidence,
            MinScore = thresholds.MinScore,
            AutoCalcTrailThreshold = thresholds.AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = thresholds.AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = thresholds.AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = thresholds.AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = thresholds.AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = thresholds.AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = thresholds.AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = thresholds.AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = thresholds.AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = thresholds.AutoCalcScoreThreshold,
        };
    }

    private static SettingsProfile CloneProfile(SettingsProfile profile)
    {
        return new SettingsProfile
        {
            Name = SettingsProfile.NormalizeName(profile.Name),
            Thresholds = CloneThresholds(profile.Thresholds),
            FilterThresholds = profile.FilterThresholds
                .Select(t => new ProfileFilterThresholds
                {
                    Key = t.Key,
                    Thresholds = CloneThresholds(t.Thresholds)
                })
                .ToList(),
            IncludeSubfoldersDefault = profile.IncludeSubfoldersDefault,
            WatchFolderDefault = profile.WatchFolderDefault,
            StfTargetBackgroundDefault = profile.StfTargetBackgroundDefault,
            ShowTrailSlider = profile.ShowTrailSlider,
            ShowFwhmSlider = profile.ShowFwhmSlider,
            ShowFwhmArcsecSlider = profile.ShowFwhmArcsecSlider,
            ShowSqmSlider = profile.ShowSqmSlider,
            ShowSkyTempSlider = profile.ShowSkyTempSlider,
            ShowHfrSlider = profile.ShowHfrSlider,
            ShowEccentricitySlider = profile.ShowEccentricitySlider,
            ShowMeanBackgroundSlider = profile.ShowMeanBackgroundSlider,
            ShowStarsSlider = profile.ShowStarsSlider,
            ShowScoreSlider = profile.ShowScoreSlider,
            ShowTrailMetric = profile.ShowTrailMetric,
            ShowFwhmMetric = profile.ShowFwhmMetric,
            ShowFwhmArcsecMetric = profile.ShowFwhmArcsecMetric,
            ShowSqmMetric = profile.ShowSqmMetric,
            ShowSkyTempMetric = profile.ShowSkyTempMetric,
            ShowHfrMetric = profile.ShowHfrMetric,
            ShowEccentricityMetric = profile.ShowEccentricityMetric,
            ShowMeanBackgroundMetric = profile.ShowMeanBackgroundMetric,
            ShowStarsMetric = profile.ShowStarsMetric,
            ShowScoreMetric = profile.ShowScoreMetric,
            UseScoreFwhm = profile.UseScoreFwhm,
            UseScoreEccentricity = profile.UseScoreEccentricity,
            UseScoreTrail = profile.UseScoreTrail,
            UseScoreHfr = profile.UseScoreHfr,
            UseScoreStars = profile.UseScoreStars,
            UseScoreMeanBackground = profile.UseScoreMeanBackground,
            ScoreWeightFwhm = profile.ScoreWeightFwhm,
            ScoreWeightEccentricity = profile.ScoreWeightEccentricity,
            ScoreWeightTrail = profile.ScoreWeightTrail,
            ScoreWeightHfr = profile.ScoreWeightHfr,
            ScoreWeightStars = profile.ScoreWeightStars,
            ScoreWeightMeanBackground = profile.ScoreWeightMeanBackground,
            AutoCalcTrailThreshold = profile.AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = profile.AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = profile.AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = profile.AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = profile.AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = profile.AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = profile.AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = profile.AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = profile.AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = profile.AutoCalcScoreThreshold,
        };
    }

    private SettingsProfile CreateProfileFromCurrentState(string name)
    {
        return new SettingsProfile
        {
            Name = SettingsProfile.NormalizeName(name),
            Thresholds = CloneThresholds(GetSettingsThresholds()),
            FilterThresholds = _filterThresholds
                .Select(kvp => new ProfileFilterThresholds
                {
                    Key = kvp.Key,
                    Thresholds = CloneThresholds(kvp.Value)
                })
                .ToList(),
            IncludeSubfoldersDefault = IncludeSubfolders,
            WatchFolderDefault = WatchFolderEnabled,
            StfTargetBackgroundDefault = StfTargetBackground,
            ShowTrailSlider = ShowTrailSlider,
            ShowFwhmSlider = ShowFwhmSlider,
            ShowFwhmArcsecSlider = ShowFwhmArcsecSlider,
            ShowSqmSlider = ShowSqmSlider,
            ShowSkyTempSlider = ShowSkyTempSlider,
            ShowHfrSlider = ShowHfrSlider,
            ShowEccentricitySlider = ShowEccentricitySlider,
            ShowMeanBackgroundSlider = ShowMeanBackgroundSlider,
            ShowStarsSlider = ShowStarsSlider,
            ShowScoreSlider = ShowScoreSlider,
            ShowTrailMetric = ShowTrailMetric,
            ShowFwhmMetric = ShowFwhmMetric,
            ShowFwhmArcsecMetric = ShowFwhmArcsecMetric,
            ShowSqmMetric = ShowSqmMetric,
            ShowSkyTempMetric = ShowSkyTempMetric,
            ShowHfrMetric = ShowHfrMetric,
            ShowEccentricityMetric = ShowEccentricityMetric,
            ShowMeanBackgroundMetric = ShowMeanBackgroundMetric,
            ShowStarsMetric = ShowStarsMetric,
            ShowScoreMetric = ShowScoreMetric,
            UseScoreFwhm = UseScoreFwhm,
            UseScoreEccentricity = UseScoreEccentricity,
            UseScoreTrail = UseScoreTrail,
            UseScoreHfr = UseScoreHfr,
            UseScoreStars = UseScoreStars,
            UseScoreMeanBackground = UseScoreMeanBackground,
            ScoreWeightFwhm = ScoreWeightFwhm,
            ScoreWeightEccentricity = ScoreWeightEccentricity,
            ScoreWeightTrail = ScoreWeightTrail,
            ScoreWeightHfr = ScoreWeightHfr,
            ScoreWeightStars = ScoreWeightStars,
            ScoreWeightMeanBackground = ScoreWeightMeanBackground,
            AutoCalcTrailThreshold = AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = AutoCalcScoreThreshold,
        };
    }

    private void ApplySelectedProfileSettings(SettingsProfile profile)
    {
        _isApplyingProfile = true;
        try
        {
            _filterThresholds.Clear();

            if (profile.FilterThresholds.Count > 0)
            {
                foreach (var filterThresholds in profile.FilterThresholds)
                {
                    _filterThresholds[NormalizeFilterValue(filterThresholds.Key)] = CloneThresholds(filterThresholds.Thresholds);
                }
            }
            else
            {
                _filterThresholds[string.Empty] = CloneThresholds(profile.Thresholds);
            }

            if (!_filterThresholds.ContainsKey(string.Empty))
            {
                _filterThresholds[string.Empty] = CloneThresholds(profile.Thresholds);
            }

            IncludeSubfolders = profile.IncludeSubfoldersDefault;
            WatchFolderEnabled = profile.WatchFolderDefault;
            StfTargetBackground = profile.StfTargetBackgroundDefault;

            ShowTrailSlider = profile.ShowTrailSlider;
            ShowFwhmSlider = profile.ShowFwhmSlider;
            ShowFwhmArcsecSlider = profile.ShowFwhmArcsecSlider;
            ShowSqmSlider = profile.ShowSqmSlider;
            ShowSkyTempSlider = profile.ShowSkyTempSlider;
            ShowHfrSlider = profile.ShowHfrSlider;
            ShowEccentricitySlider = profile.ShowEccentricitySlider;
            ShowMeanBackgroundSlider = profile.ShowMeanBackgroundSlider;
            ShowStarsSlider = profile.ShowStarsSlider;
            ShowScoreSlider = profile.ShowScoreSlider;
            ShowTrailMetric = profile.ShowTrailMetric;
            ShowFwhmMetric = profile.ShowFwhmMetric;
            ShowFwhmArcsecMetric = profile.ShowFwhmArcsecMetric;
            ShowSqmMetric = profile.ShowSqmMetric;
            ShowSkyTempMetric = profile.ShowSkyTempMetric;
            ShowHfrMetric = profile.ShowHfrMetric;
            ShowEccentricityMetric = profile.ShowEccentricityMetric;
            ShowMeanBackgroundMetric = profile.ShowMeanBackgroundMetric;
            ShowStarsMetric = profile.ShowStarsMetric;
            ShowScoreMetric = profile.ShowScoreMetric;
            UseScoreFwhm = profile.UseScoreFwhm;
            UseScoreEccentricity = profile.UseScoreEccentricity;
            UseScoreTrail = profile.UseScoreTrail;
            UseScoreHfr = profile.UseScoreHfr;
            UseScoreStars = profile.UseScoreStars;
            UseScoreMeanBackground = profile.UseScoreMeanBackground;
            ScoreWeightFwhm = profile.ScoreWeightFwhm;
            ScoreWeightEccentricity = profile.ScoreWeightEccentricity;
            ScoreWeightTrail = profile.ScoreWeightTrail;
            ScoreWeightHfr = profile.ScoreWeightHfr;
            ScoreWeightStars = profile.ScoreWeightStars;
            ScoreWeightMeanBackground = profile.ScoreWeightMeanBackground;

            RaiseAllThresholdPropertiesChanged();
            UpdateFrameComparisons();
            ApplyThresholds();
        }
        finally
        {
            _isApplyingProfile = false;
        }

        PersistAppSettings();
    }

    private void PersistProfileUiFlags()
    {
        if (_isApplyingProfile || _isLoadingInitialSettings || SelectedSettingsProfile is null)
        {
            return;
        }

        SelectedSettingsProfile.ShowTrailSlider = ShowTrailSlider;
        SelectedSettingsProfile.ShowFwhmSlider = ShowFwhmSlider;
        SelectedSettingsProfile.ShowFwhmArcsecSlider = ShowFwhmArcsecSlider;
        SelectedSettingsProfile.ShowSqmSlider = ShowSqmSlider;
        SelectedSettingsProfile.ShowSkyTempSlider = ShowSkyTempSlider;
        SelectedSettingsProfile.ShowHfrSlider = ShowHfrSlider;
        SelectedSettingsProfile.ShowEccentricitySlider = ShowEccentricitySlider;
        SelectedSettingsProfile.ShowMeanBackgroundSlider = ShowMeanBackgroundSlider;
        SelectedSettingsProfile.ShowStarsSlider = ShowStarsSlider;
        SelectedSettingsProfile.ShowScoreSlider = ShowScoreSlider;
        SelectedSettingsProfile.ShowTrailMetric = ShowTrailMetric;
        SelectedSettingsProfile.ShowFwhmMetric = ShowFwhmMetric;
        SelectedSettingsProfile.ShowFwhmArcsecMetric = ShowFwhmArcsecMetric;
        SelectedSettingsProfile.ShowSqmMetric = ShowSqmMetric;
        SelectedSettingsProfile.ShowSkyTempMetric = ShowSkyTempMetric;
        SelectedSettingsProfile.ShowHfrMetric = ShowHfrMetric;
        SelectedSettingsProfile.ShowEccentricityMetric = ShowEccentricityMetric;
        SelectedSettingsProfile.ShowMeanBackgroundMetric = ShowMeanBackgroundMetric;
        SelectedSettingsProfile.ShowStarsMetric = ShowStarsMetric;
        SelectedSettingsProfile.ShowScoreMetric = ShowScoreMetric;
        SelectedSettingsProfile.UseScoreFwhm = UseScoreFwhm;
        SelectedSettingsProfile.UseScoreEccentricity = UseScoreEccentricity;
        SelectedSettingsProfile.UseScoreTrail = UseScoreTrail;
        SelectedSettingsProfile.UseScoreHfr = UseScoreHfr;
        SelectedSettingsProfile.UseScoreStars = UseScoreStars;
        SelectedSettingsProfile.UseScoreMeanBackground = UseScoreMeanBackground;
        SelectedSettingsProfile.ScoreWeightFwhm = ScoreWeightFwhm;
        SelectedSettingsProfile.ScoreWeightEccentricity = ScoreWeightEccentricity;
        SelectedSettingsProfile.ScoreWeightTrail = ScoreWeightTrail;
        SelectedSettingsProfile.ScoreWeightHfr = ScoreWeightHfr;
        SelectedSettingsProfile.ScoreWeightStars = ScoreWeightStars;
        SelectedSettingsProfile.ScoreWeightMeanBackground = ScoreWeightMeanBackground;
        SelectedSettingsProfile.AutoCalcTrailThreshold = SettingsAutoCalcTrailThreshold;
        SelectedSettingsProfile.AutoCalcFwhmThreshold = SettingsAutoCalcFwhmThreshold;
        SelectedSettingsProfile.AutoCalcFwhmArcsecThreshold = SettingsAutoCalcFwhmArcsecThreshold;
        SelectedSettingsProfile.AutoCalcSqmThreshold = SettingsAutoCalcSqmThreshold;
        SelectedSettingsProfile.AutoCalcSkyTempThreshold = SettingsAutoCalcSkyTempThreshold;
        SelectedSettingsProfile.AutoCalcHfrThreshold = SettingsAutoCalcHfrThreshold;
        SelectedSettingsProfile.AutoCalcEccentricityThreshold = SettingsAutoCalcEccentricityThreshold;
        SelectedSettingsProfile.AutoCalcMeanBackgroundThreshold = SettingsAutoCalcMeanBackgroundThreshold;
        SelectedSettingsProfile.AutoCalcStarsThreshold = SettingsAutoCalcStarsThreshold;
        SelectedSettingsProfile.AutoCalcScoreThreshold = SettingsAutoCalcScoreThreshold;
        SelectedSettingsProfile.IncludeSubfoldersDefault = IncludeSubfolders;
        SelectedSettingsProfile.WatchFolderDefault = WatchFolderEnabled;
        SelectedSettingsProfile.StfTargetBackgroundDefault = StfTargetBackground;

        PersistAppSettings();
    }

    private void PersistAppSettings()
    {
        if (!_isApplyingProfile && SelectedSettingsProfile is not null)
        {
            var snapshot = CreateProfileFromCurrentState(SelectedSettingsProfile.Name);
            var index = SettingsProfiles.IndexOf(SelectedSettingsProfile);
            if (index >= 0)
            {
                SettingsProfiles[index] = snapshot;
                _selectedSettingsProfile = snapshot;
                OnPropertyChanged(nameof(SelectedSettingsProfile));
                OnPropertyChanged(nameof(SelectedProfileName));
                OnPropertyChanged(nameof(IsSelectedProfileDefault));
            }
        }

        _settings.Save(new AppSettings
        {
            InputFolder = InputFolder,
            RejectedFolder = RejectedFolder,
            IncludeSubfolders = IncludeSubfolders,
            WatchFolder = WatchFolderEnabled,
            DefaultProfileName = DefaultProfileName,
            Profiles = SettingsProfiles.Select(CloneProfile).ToList()
        });
    }

    public double MaxFwhm
    {
        get => GetScopedDouble(t => t.MaxFwhm);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcFwhmThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcFwhmThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxFwhm) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxFwhm = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MaxFwhmArcsec
    {
        get => GetScopedDouble(t => t.MaxFwhmArcsec);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcFwhmArcsecThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcFwhmArcsecThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxFwhmArcsec) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxFwhmArcsec = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MaxSkyTemp
    {
        get => GetScopedDouble(t => t.MaxSkyTemp);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcSkyTempThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcSkyTempThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxSkyTemp) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxSkyTemp = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MaxHfr
    {
        get => GetScopedDouble(t => t.MaxHfr);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcHfrThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcHfrThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxHfr) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxHfr = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MinSqm
    {
        get => GetScopedDouble(t => t.MinSqm);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcSqmThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcSqmThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MinSqm) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MinSqm = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MaxEccentricity
    {
        get => GetScopedDouble(t => t.MaxEccentricity);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcEccentricityThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcEccentricityThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxEccentricity) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxEccentricity = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MaxMeanBackground
    {
        get => GetScopedDouble(t => t.MaxMeanBackground);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcMeanBackgroundThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcMeanBackgroundThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MaxMeanBackground) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MaxMeanBackground = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MinStars
    {
        get => GetScopedDouble(t => t.MinStars);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcStarsThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcStarsThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MinStars) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MinStars = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double MinScore
    {
        get => GetScopedDouble(t => t.MinScore);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcScoreThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcScoreThreshold));
            if (Math.Abs(GetScopedDouble(t => t.MinScore) - value) < double.Epsilon) return;
            SetScopedDouble((t, v) => t.MinScore = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public bool RejectSatelliteTrail
    {
        get => _rejectSatelliteTrail;
        set
        {
            if (_rejectSatelliteTrail == value) return;
            _rejectSatelliteTrail = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public int MinSatelliteConfidence
    {
        get => GetScopedInt(t => t.MinSatelliteConfidence);
        set
        {
            SetScopedAutoCalcFlag((t, v) => t.AutoCalcTrailThreshold = v, false);
            OnPropertyChanged(nameof(AutoCalcTrailThreshold));
            if (GetScopedInt(t => t.MinSatelliteConfidence) == value) return;
            SetScopedInt((t, v) => t.MinSatelliteConfidence = v, value);
            OnPropertyChanged();
            ApplyThresholds();
            PersistAppSettings();
        }
    }

    public double? SessionFocalLengthMm
    {
        get => _sessionFocalLengthMm;
        private set
        {
            if (_sessionFocalLengthMm == value) return;
            _sessionFocalLengthMm = value;
            OnPropertyChanged();
        }
    }

    public double? SessionPixelSizeUm
    {
        get => _sessionPixelSizeUm;
        private set
        {
            if (_sessionPixelSizeUm == value) return;
            _sessionPixelSizeUm = value;
            OnPropertyChanged();
        }
    }

    public ICommand BrowseInputCommand { get; }
    public ICommand BrowseRejectedCommand { get; }
    public ICommand LoadFramesCommand { get; }
    public ICommand MoveRejectedCommand { get; }
    public ICommand OpenPreviewCommand { get; }
    public ICommand ToggleRejectCommand { get; }
    public ICommand ApplyAutoStretchCommand { get; }
    public ICommand AddSortRuleCommand { get; }
    public ICommand RemoveSortRuleCommand { get; }
    public ICommand SaveSessionCommand { get; }
    public ICommand LoadSessionCommand { get; }
    public ICommand DebugShowUpdateBannerCommand { get; }

    public MainViewModel()
    {
        FilteredFrames = CollectionViewSource.GetDefaultView(Frames);
        FilteredFrames.Filter = FilterFrame;

        BrowseInputCommand = new RelayCommand(_ => BrowseInput());
        BrowseRejectedCommand = new RelayCommand(_ => BrowseRejected());
        LoadFramesCommand = new RelayCommand(async _ => await LoadFramesAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(InputFolder));
        MoveRejectedCommand = new RelayCommand(_ => ExecuteMoveRejected(), _ => !IsBusy && Frames.Any(f => f.IsRejected) && !string.IsNullOrWhiteSpace(RejectedFolder));
        OpenPreviewCommand = new RelayCommand(async p => await OpenPreviewAsync(p as FrameItem));
        ToggleRejectCommand = new RelayCommand(p => ToggleFrameReject(p as FrameItem), p => p is FrameItem);
        ApplyAutoStretchCommand = new RelayCommand(async _ => await ApplyAutoStretchAsync(), _ => _loadedFrames.Count > 0);
        AddSortRuleCommand = new RelayCommand(_ => AddSortRule(), _ => SortRules.Count < SortFieldOptions.Count);
        RemoveSortRuleCommand = new RelayCommand(rule => RemoveSortRule(rule as FrameSortRuleViewModel), rule => rule is FrameSortRuleViewModel && SortRules.Count > 1);
        SaveSessionCommand = new RelayCommand(_ => SaveSession(), _ => Frames.Count > 0 && !IsBusy);
        LoadSessionCommand = new RelayCommand(async _ => await LoadSessionAsync(), _ => !IsBusy);
        DebugShowUpdateBannerCommand = new RelayCommand(async _ => await ShowDebugUpdateBannerAsync());
        AddSettingsProfileCommand = new RelayCommand(_ => AddSettingsProfile(), _ => CanAddSettingsProfile());

        AddSortRule(initialField: DefaultPrimarySortField, initialDirection: SortDirectionOptions[0]);

        _isLoadingInitialSettings = true;
        try
        {
            var settings = _settings.Load();
            InputFolder = settings.InputFolder;
            RejectedFolder = settings.RejectedFolder;
            _includeSubfolders = settings.IncludeSubfolders;
            _watchFolderEnabled = settings.WatchFolder;

            SettingsProfiles.Clear();
            var loadedProfiles = settings.Profiles is { Count: > 0 }
                ? settings.Profiles
                : [new SettingsProfile { Name = "Default" }];
            foreach (var profile in loadedProfiles)
            {
                SettingsProfiles.Add(CloneProfile(profile));
            }

            DefaultProfileName = SettingsProfile.NormalizeName(settings.DefaultProfileName);
            var defaultProfile = SettingsProfiles.FirstOrDefault(p => string.Equals(p.Name, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                                 ?? SettingsProfiles.First();
            SelectedSettingsProfile = defaultProfile;
            NewProfileName = defaultProfile.Name;
        }
        finally
        {
            _isLoadingInitialSettings = false;
        }

        // Fire-and-forget update check — deferred to let UI show first.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await CheckForUpdateAsync();
        });
    }

    private async Task CheckForUpdateAsync()
    {
        var info = await _updateCheck.GetLatestUpdateAsync();
        if (info is not null)
            System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateBanner.ShowUpdate(info));
    }

    private async Task ShowDebugUpdateBannerAsync()
    {
        var liveInfo = await _updateCheck.GetLatestReleaseInfoAsync();
        if (liveInfo is not null)
        {
            UpdateBanner.ShowUpdate(liveInfo);
            return;
        }

        var stamped = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var fallbackVersion = stamped is null || stamped == new Version(1, 0, 0, 0)
            ? "debug"
            : $"{stamped.Major}.{stamped.Minor}.{stamped.Build + 1}";

        var info = new UpdateInfo(
            fallbackVersion,
            "### Debug update banner\n\nUnable to fetch the latest release notes from GitHub. This is a local fallback preview.",
            UpdateCheckService.ReleasesPageUrl);

        UpdateBanner.ShowUpdate(info);
    }

    private bool FilterFrame(object item)
    {
        if (item is not FrameItem frame)
        {
            return false;
        }

        return IsFrameVisible(frame);
    }

    private void RebuildFilterChips()
    {
        _isUpdatingFilterSelection = true;
        try
        {
            foreach (var chip in FilterChips)
            {
                chip.PropertyChanged -= FilterChip_PropertyChanged;
            }

            FilterChips.Clear();

            var filters = Frames
                .Select(frame =>
                {
                    if (!NormalizeFilterKey(frame.FilterName, out var displayName))
                    {
                        return default;
                    }
                    var key = NormalizeFilterValue(frame.FilterName);
                    var category = FilterClassifier.Classify(key);
                    var canonical = FilterClassifier.GetCanonicalDisplay(category, displayName!);
                    return (Key: key, DisplayName: canonical, Category: category);
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => FilterClassifier.GetSortOrder(x.Category))
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var filter in filters)
            {
                var chip = new FilterChipViewModel(filter.Key!, filter.DisplayName!, isSelected: true, filter.Category);
                chip.PropertyChanged += FilterChip_PropertyChanged;
                FilterChips.Add(chip);
            }
        }
        finally
        {
            _isUpdatingFilterSelection = false;
        }

        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasMultipleFilterChips));

        RebuildRejectionScopeChips();
        FilteredFrames.Refresh();
        UpdateFrameStatistics();
        RefreshPreviewVisibleFrames();
    }

    private void FilterChip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingFilterSelection || e.PropertyName != nameof(FilterChipViewModel.IsSelected))
        {
            return;
        }

        FilteredFrames.Refresh();
        UpdateFrameStatistics();
        RefreshPreviewVisibleFrames();
    }

    private void AddSortRule(SortFieldOption? initialField = null, SortDirectionOption? initialDirection = null)
    {
        if (SortRules.Count >= SortFieldOptions.Count)
        {
            return;
        }

        var field = initialField
            ?? SortFieldOptions.FirstOrDefault(option => SortRules.All(rule => rule.SelectedField.Value != option.Value))
            ?? SortFieldOptions[0];
        var direction = initialDirection ?? SortDirectionOptions[0];

        var rule = new FrameSortRuleViewModel(field, direction);
        rule.PropertyChanged += SortRule_PropertyChanged;
        SortRules.Add(rule);
        ApplySorting();
        RaiseSortCommandStateChanged();
    }

    private void RemoveSortRule(FrameSortRuleViewModel? rule)
    {
        if (rule is null || SortRules.Count <= 1)
        {
            return;
        }

        rule.PropertyChanged -= SortRule_PropertyChanged;
        SortRules.Remove(rule);
        ApplySorting();
        RaiseSortCommandStateChanged();
    }

    private void SortRule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FrameSortRuleViewModel.SelectedField) or nameof(FrameSortRuleViewModel.SelectedDirection))
        {
            ApplySorting();
            RaiseSortCommandStateChanged();
        }
    }

    private void ApplySorting()
    {
        if (FilteredFrames is ListCollectionView collectionView)
        {
            var rules = SortRules
                .Select(rule => new SortRuleSnapshot(rule.SelectedField.Value, rule.SelectedDirection.Value))
                .ToArray();
            collectionView.CustomSort = new FrameItemComparer(rules);
        }

        FilteredFrames.Refresh();
        RefreshPreviewVisibleFrames();
    }

    private void RaiseSortCommandStateChanged()
    {
        ((RelayCommand)AddSortRuleCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RemoveSortRuleCommand).RaiseCanExecuteChanged();
    }

    private bool IsFrameVisible(FrameItem frame)
    {
        return ((frame.IsRejected && ShowRejected) || (!frame.IsRejected && ShowAccepted))
               && IsFilterSelected(frame);
    }

    /// <summary>All frames matching the active filter chips, ignoring accepted/rejected visibility toggles.</summary>
    private IEnumerable<FrameItem> GetAllFilteredFrames()
    {
        return Frames.Where(IsFilterSelected);
    }

    private IEnumerable<FrameItem> GetVisibleFramesForStatistics()
    {
        return GetAllFilteredFrames();
    }

    private bool IsFilterSelected(FrameItem frame)
    {
        if (FilterChips.Count == 0)
        {
            return true;
        }

        var key = NormalizeFilterValue(frame.FilterName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var chip = FilterChips.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return chip?.IsSelected ?? false;
    }

    private static bool NormalizeFilterKey(string? filterName, out string? displayName)
    {
        displayName = NormalizeFilterValue(filterName);
        return !string.IsNullOrWhiteSpace(displayName);
    }

    private static string NormalizeFilterValue(string? filterName)
    {
        return string.IsNullOrWhiteSpace(filterName)
            ? string.Empty
            : filterName.Trim();
    }

    // ---------- Per-filter (scoped) rejection thresholds ----------

    /// <summary>Filter keys currently selected in the rejection scope dropdown. Empty = none selected (treated as all).</summary>
    private List<string> GetSelectedScopeKeys()
    {
        if (RejectionScopeChips.Count == 0) return [string.Empty];
        var selected = RejectionScopeChips.Where(c => c.IsSelected).Select(c => c.Key).ToList();
        return selected.Count == 0 ? RejectionScopeChips.Select(c => c.Key).ToList() : selected;
    }

    /// <summary>All distinct normalized filter keys present in the current frames (empty string = "(no filter)").</summary>
    private List<string> GetAllFilterKeys()
    {
        return Frames
            .Select(f => NormalizeFilterValue(f.FilterName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Thresholds GetThresholdsForKey(string key)
    {
        if (!_filterThresholds.TryGetValue(key, out var t))
        {
            if (!string.IsNullOrEmpty(key) && _filterThresholds.TryGetValue(string.Empty, out var baseThresholds))
            {
                t = CloneThresholds(baseThresholds);
            }
            else
            {
                t = new Thresholds
                {
                    MaxFwhm = 8.0,
                    MaxFwhmArcsec = 20.0,
                    MaxHfr = 4.5,
                    MaxEccentricity = 0.6,
                    MaxMeanBackground = 2000.0,
                    MinStars = 0,
                    MinSqm = 0,
                    MaxSkyTemp = 40.0,
                    MinSatelliteConfidence = 80,
                    MinScore = 0.0,
                    AutoCalcTrailThreshold = true,
                    AutoCalcFwhmThreshold = true,
                    AutoCalcFwhmArcsecThreshold = true,
                    AutoCalcSqmThreshold = true,
                    AutoCalcSkyTempThreshold = true,
                    AutoCalcHfrThreshold = true,
                    AutoCalcEccentricityThreshold = true,
                    AutoCalcMeanBackgroundThreshold = true,
                    AutoCalcStarsThreshold = true,
                    AutoCalcScoreThreshold = true,
                };
            }

            _filterThresholds[key] = t;
        }
        return t;
    }

    /// <summary>Aggregated threshold value across the currently selected scope. Returns the value of the first in-scope group.</summary>
    private double GetScopedDouble(Func<Thresholds, double> selector)
    {
        var keys = GetSelectedScopeKeys();
        var first = keys.FirstOrDefault() ?? string.Empty;
        return selector(GetThresholdsForKey(first));
    }

    private int GetScopedInt(Func<Thresholds, int> selector)
    {
        var keys = GetSelectedScopeKeys();
        var first = keys.FirstOrDefault() ?? string.Empty;
        return selector(GetThresholdsForKey(first));
    }

    private bool GetScopedThreshold(Func<Thresholds, bool> selector)
    {
        var keys = GetSelectedScopeKeys();
        var first = keys.FirstOrDefault() ?? string.Empty;
        return selector(GetThresholdsForKey(first));
    }

    private void SetScopedThreshold(Action<Thresholds, bool> assign, bool value, string propertyName)
    {
        var keys = GetSelectedScopeKeys();
        foreach (var key in keys)
        {
            assign(GetThresholdsForKey(key), value);
        }

        var allScopeSelected = RejectionScopeChips.Count == 0 || keys.Count == RejectionScopeChips.Count;
        if (allScopeSelected && !keys.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            assign(GetThresholdsForKey(string.Empty), value);
        }

        OnPropertyChanged(propertyName);
        PersistAppSettings();
        InitializeThresholdsFromLoadedFrames();
        ApplyThresholds();
    }

    private void SetScopedDouble(Action<Thresholds, double> assign, double value)
    {
        var keys = GetSelectedScopeKeys();
        foreach (var key in keys)
        {
            assign(GetThresholdsForKey(key), value);
        }

        var allScopeSelected = RejectionScopeChips.Count == 0 || keys.Count == RejectionScopeChips.Count;
        if (allScopeSelected && !keys.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            assign(GetThresholdsForKey(string.Empty), value);
        }
    }

    private void SetScopedInt(Action<Thresholds, int> assign, int value)
    {
        var keys = GetSelectedScopeKeys();
        foreach (var key in keys)
        {
            assign(GetThresholdsForKey(key), value);
        }

        var allScopeSelected = RejectionScopeChips.Count == 0 || keys.Count == RejectionScopeChips.Count;
        if (allScopeSelected && !keys.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            assign(GetThresholdsForKey(string.Empty), value);
        }
    }

    private void SetScopedAutoCalcFlag(Action<Thresholds, bool> assign, bool value)
    {
        var keys = GetSelectedScopeKeys();
        foreach (var key in keys)
        {
            assign(GetThresholdsForKey(key), value);
        }

        var allScopeSelected = RejectionScopeChips.Count == 0 || keys.Count == RejectionScopeChips.Count;
        if (allScopeSelected && !keys.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            assign(GetThresholdsForKey(string.Empty), value);
        }
    }

    private void RebuildRejectionScopeChips()
    {
        _isUpdatingRejectionScope = true;
        try
        {
            foreach (var chip in RejectionScopeChips)
            {
                chip.PropertyChanged -= RejectionScopeChip_PropertyChanged;
            }
            RejectionScopeChips.Clear();
            RejectionScopeGroups.Clear();

            var keys = GetAllFilterKeys();
            // Sort by classifier order so narrowband appears before LRGB, then unknown.
            var named = keys
                .Where(k => k.Length > 0)
                .Select(k => (Key: k, Category: FilterClassifier.Classify(k)))
                .OrderBy(x => FilterClassifier.GetSortOrder(x.Category))
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var hasUnfiltered = keys.Any(k => k.Length == 0);

            foreach (var (key, category) in named)
            {
                var display = FilterClassifier.GetCanonicalDisplay(category, key);
                var chip = new FilterChipViewModel(key, display, isSelected: true, category);
                chip.PropertyChanged += RejectionScopeChip_PropertyChanged;
                RejectionScopeChips.Add(chip);
            }
            if (hasUnfiltered)
            {
                var chip = new FilterChipViewModel(string.Empty, "(no filter)", isSelected: true, FilterCategory.Unknown);
                chip.PropertyChanged += RejectionScopeChip_PropertyChanged;
                RejectionScopeChips.Add(chip);
            }

            // Group for the dropdown: Narrowband, LRGB, Other.
            foreach (var groupVm in RejectionScopeChips
                         .GroupBy(c => c.Group)
                         .OrderBy(g => g.First().SortOrder)
                         .Select(g => new FilterChipGroupViewModel(g.Key, FilterClassifier.GetGroupDisplay(g.Key))
                         {
                         }))
            {
                foreach (var c in RejectionScopeChips.Where(c => c.Group == groupVm.Group))
                {
                    groupVm.Chips.Add(c);
                }
                RejectionScopeGroups.Add(groupVm);
            }

            // Ensure each key has a thresholds entry.
            foreach (var key in keys) GetThresholdsForKey(key);
        }
        finally
        {
            _isUpdatingRejectionScope = false;
        }

        OnPropertyChanged(nameof(HasRejectionScopeChips));
        OnPropertyChanged(nameof(RejectionScopeSummary));
        RaiseAllThresholdPropertiesChanged();
    }

    private void RejectionScopeChip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingRejectionScope || e.PropertyName != nameof(FilterChipViewModel.IsSelected)) return;
        OnPropertyChanged(nameof(RejectionScopeSummary));
        RaiseAllThresholdPropertiesChanged();
    }

    private void RaiseAllThresholdPropertiesChanged()
    {
        OnPropertyChanged(nameof(MaxFwhm));
        OnPropertyChanged(nameof(MaxFwhmArcsec));
        OnPropertyChanged(nameof(MaxHfr));
        OnPropertyChanged(nameof(MaxEccentricity));
        OnPropertyChanged(nameof(MaxMeanBackground));
        OnPropertyChanged(nameof(MinStars));
        OnPropertyChanged(nameof(MinSqm));
        OnPropertyChanged(nameof(MaxSkyTemp));
        OnPropertyChanged(nameof(MinScore));
        OnPropertyChanged(nameof(MinSatelliteConfidence));
        OnPropertyChanged(nameof(AutoCalcTrailThreshold));
        OnPropertyChanged(nameof(AutoCalcFwhmThreshold));
        OnPropertyChanged(nameof(AutoCalcFwhmArcsecThreshold));
        OnPropertyChanged(nameof(AutoCalcSqmThreshold));
        OnPropertyChanged(nameof(AutoCalcSkyTempThreshold));
        OnPropertyChanged(nameof(AutoCalcHfrThreshold));
        OnPropertyChanged(nameof(AutoCalcEccentricityThreshold));
        OnPropertyChanged(nameof(AutoCalcMeanBackgroundThreshold));
        OnPropertyChanged(nameof(AutoCalcStarsThreshold));
        OnPropertyChanged(nameof(AutoCalcScoreThreshold));
        OnPropertyChanged(nameof(SettingsMinSatelliteConfidence));
        OnPropertyChanged(nameof(SettingsMaxFwhm));
        OnPropertyChanged(nameof(SettingsMaxFwhmArcsec));
        OnPropertyChanged(nameof(SettingsMinSqm));
        OnPropertyChanged(nameof(SettingsMaxSkyTemp));
        OnPropertyChanged(nameof(SettingsMaxHfr));
        OnPropertyChanged(nameof(SettingsMaxEccentricity));
        OnPropertyChanged(nameof(SettingsMaxMeanBackground));
        OnPropertyChanged(nameof(SettingsMinStars));
        OnPropertyChanged(nameof(SettingsMinScore));
        OnPropertyChanged(nameof(SettingsAutoCalcTrailThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcFwhmThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcFwhmArcsecThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcSqmThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcSkyTempThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcHfrThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcEccentricityThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcMeanBackgroundThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcStarsThreshold));
        OnPropertyChanged(nameof(SettingsAutoCalcScoreThreshold));
    }

    public bool HasRejectionScopeChips => RejectionScopeChips.Count > 1;

    public string RejectionScopeSummary
    {
        get
        {
            if (RejectionScopeChips.Count == 0) return "All Filters";
            var selected = RejectionScopeChips.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0 || selected.Count == RejectionScopeChips.Count) return "All Filters";
            return selected.Count == 1 ? selected[0].DisplayName : $"{selected.Count} filters";
        }
    }

    public ICommand ResetThresholdsCommand => _resetThresholdsCommand ??= new RelayCommand(_ => ResetThresholdsForScope());
    private ICommand? _resetThresholdsCommand;

    /// <summary>Selects every chip in the rejection scope dropdown.</summary>
    public ICommand SelectAllScopeCommand => _selectAllScopeCommand ??= new RelayCommand(_ => SetRejectionScopeSelection(_ => true));
    private ICommand? _selectAllScopeCommand;

    /// <summary>Selects only narrowband filters (Ha / Oiii / Sii) in the rejection scope dropdown.</summary>
    public ICommand SelectNarrowbandScopeCommand => _selectNarrowbandScopeCommand
        ??= new RelayCommand(_ => SetRejectionScopeSelection(c => c.Group == FilterGroup.Narrowband));
    private ICommand? _selectNarrowbandScopeCommand;

    /// <summary>Selects only LRGB filters (L / R / G / B) in the rejection scope dropdown.</summary>
    public ICommand SelectLrgbScopeCommand => _selectLrgbScopeCommand
        ??= new RelayCommand(_ => SetRejectionScopeSelection(c => c.Group == FilterGroup.Lrgb));
    private ICommand? _selectLrgbScopeCommand;

    /// <summary>Selects only R / G / B filters (excluding L) in the rejection scope dropdown.</summary>
    public ICommand SelectRgbScopeCommand => _selectRgbScopeCommand
        ??= new RelayCommand(_ => SetRejectionScopeSelection(c => c.Category is FilterCategory.Red or FilterCategory.Green or FilterCategory.Blue));
    private ICommand? _selectRgbScopeCommand;

    private void SetRejectionScopeSelection(Func<FilterChipViewModel, bool> predicate)
    {
        if (RejectionScopeChips.Count == 0) return;

        _isUpdatingRejectionScope = true;
        try
        {
            foreach (var chip in RejectionScopeChips)
            {
                chip.IsSelected = predicate(chip);
            }
        }
        finally
        {
            _isUpdatingRejectionScope = false;
        }

        OnPropertyChanged(nameof(RejectionScopeSummary));
        RaiseAllThresholdPropertiesChanged();
    }

    private void ResetThresholdsForScope()
    {
        var keys = GetSelectedScopeKeys();
        if (keys.Count == 0) return;

        foreach (var key in keys)
        {
            var framesInGroup = Frames
                .Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (framesInGroup.Count == 0) continue;

            var t = GetThresholdsForKey(key);
            if (t.AutoCalcFwhmThreshold)
                t.MaxFwhm = framesInGroup.Max(f => f.Metrics.Fwhm);
            var fwhmArcsecValues = framesInGroup.Where(f => f.Metrics.FwhmArcsec.HasValue).Select(f => f.Metrics.FwhmArcsec!.Value).ToList();
            if (t.AutoCalcFwhmArcsecThreshold)
                t.MaxFwhmArcsec = fwhmArcsecValues.Count > 0 ? fwhmArcsecValues.Max() : 20.0;
            if (t.AutoCalcHfrThreshold)
                t.MaxHfr = framesInGroup.Max(f => f.Metrics.Hfr);
            if (t.AutoCalcEccentricityThreshold)
                t.MaxEccentricity = framesInGroup.Max(f => f.Metrics.Eccentricity);
            if (t.AutoCalcMeanBackgroundThreshold)
                t.MaxMeanBackground = framesInGroup.Max(f => f.Metrics.MeanBackground);
            if (t.AutoCalcStarsThreshold)
                t.MinStars = framesInGroup.Min(f => (double)f.Metrics.StarCount);

            var sqm = framesInGroup.Where(f => f.Metrics.Sqm.HasValue).Select(f => f.Metrics.Sqm!.Value).ToList();
            if (t.AutoCalcSqmThreshold)
                t.MinSqm = sqm.Count > 0 ? sqm.Min() : 0.0;

            var skyTemp = framesInGroup.Where(f => f.Metrics.SkyTemp.HasValue).Select(f => f.Metrics.SkyTemp!.Value).ToList();
            if (t.AutoCalcSkyTempThreshold)
                t.MaxSkyTemp = skyTemp.Count > 0 ? skyTemp.Max() : 40.0;

            if (t.AutoCalcScoreThreshold)
                t.MinScore = 0.0;
            if (t.AutoCalcTrailThreshold)
                t.MinSatelliteConfidence = 80;
        }

        RaiseAllThresholdPropertiesChanged();
        ApplyThresholds();
    }

    private void FrameItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FrameItem.IsRejected) or nameof(FrameItem.ManualRejectedOverride) or nameof(FrameItem.AutomaticRejected))
        {
            FilteredFrames.Refresh();
            RefreshPreviewVisibleFrames();
        }
    }

    private string? ComputeRelativePath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(dir)) return null;

        var roots = (InputFolder ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .OrderByDescending(r => r.Length);

        foreach (var root in roots)
        {
            if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && dir.Length > root.Length)
                return dir[(root.Length + 1)..];
        }

        return null;
    }

    private void BrowseInput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder(s) with FITS/XISF frames",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true && dialog.FolderNames.Length > 0)
        {
            InputFolder = string.Join(";", dialog.FolderNames);
        }
    }

    private void BrowseRejected()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Select destination folder for rejected subframes";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            RejectedFolder = dialog.SelectedPath;
        }
    }

    private void SaveFolderSettings()
    {
        PersistAppSettings();
    }

    private async Task LoadFramesAsync()
    {
        if (string.IsNullOrWhiteSpace(InputFolder))
        {
            return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressValue = 0;
        Status = "Scanning folder...";
        foreach (var frame in Frames)
        {
            frame.PropertyChanged -= FrameItem_PropertyChanged;
        }

        Frames.Clear();
        foreach (var chip in FilterChips)
        {
            chip.PropertyChanged -= FilterChip_PropertyChanged;
        }
        FilterChips.Clear();
        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasMultipleFilterChips));
        _loadedFrames.Clear();
        ResetFrameStatistics();
        SelectedFrame = null;
        _manualRoiRect = null;
        _hasManualRoi = false;
        SessionFocalLengthMm = null;
        SessionPixelSizeUm = null;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var files = _discovery.Discover(InputFolder, IncludeSubfolders);
            ProgressMaximum = Math.Max(1, files.Count);
            var loadedCount = 0;
            var skippedCount = 0;

            if (files.Count == 0)
            {
                if (WatchFolderEnabled)
                {
                    StartFolderWatch();
                    Status = "Watching empty folder for new FITS/XISF frames...";
                }
                else
                {
                    Status = "No FITS/XISF frames found.";
                }
                return;
            }

            using var reporter = new BulkLoadProgressReporter(
                files.Count,
                s => Status = s,
                v => ProgressValue = v);
            reporter.Start();

            var firstSuccessfulIndex = -1;
            RustafitsService.LoadedFrame? orientationReference = null;
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                reporter.NotifyFirstFrameStarted(Path.GetFileName(file));

                try
                {
                    var raw = await _rustafits.LoadRawFrameAsync(file, CancellationToken.None);
                    if (!raw.IsLightFrame)
                    {
                        skippedCount++;
                        reporter.NotifyFirstFrameSkipped(Path.GetFileName(file));
                        continue;
                    }
                    var metrics = _rustafits.AnalyzeFrame(raw);
                    var autoStf = _rustafits.ComputeAutoStretch(raw, _stfTargetBackground);
                    _stfShadows = autoStf.Shadows;
                    _stfMidtones = autoStf.Midtones;
                    _stfHighlights = autoStf.Highlights;
                    OnPropertyChanged(nameof(StfShadows));
                    OnPropertyChanged(nameof(StfMidtones));
                    OnPropertyChanged(nameof(StfHighlights));
                    _manualRoiRect = _rustafits.DetectRoiNormalizedRect(raw);
                    var previews = await _rustafits.RenderPreviewBitmapsAsync(raw, GetStfForFrame(raw), _manualRoiRect, metrics, CancellationToken.None);

                    var item = new FrameItem
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        RelativePath = ComputeRelativePath(file),
                        ExposureDateTime = raw.ExposureDateTime,
                        ExposureSeconds = raw.ExposureSeconds,
                        FilterName = raw.FilterName,
                        ThumbnailImage = previews.Full,
                        RoiImage = previews.Roi,
                        Metrics = metrics,
                        OrientationDebug = _rustafits.CreateOrientationReferenceDebugInfo(raw)
                    };

                    item.PropertyChanged += FrameItem_PropertyChanged;
                    Frames.Add(item);
                    _loadedFrames.Add(CreateLoadedFrameContext(item, raw, filePath: file, rotate180: false));
                    orientationReference = raw;
                    SessionFocalLengthMm ??= raw.FocalLengthMm;
                    SessionPixelSizeUm ??= raw.PixelSizeUm;
                    loadedCount++;
                    firstSuccessfulIndex = i;
                    reporter.NotifyFirstFrameCompleted(item.FileName);
                    break;
                }
                catch (Exception ex)
                {
                    skippedCount++;
                    reporter.NotifyFirstFrameSkipped(Path.GetFileName(file));
                    Debug.WriteLine($"Skipped {file}: {ex.Message}");
                }
            }

            if (firstSuccessfulIndex >= 0)
            {
                var filesToProcess = files.Skip(firstSuccessfulIndex + 1).Select((file, offset) => (File: file, SourceIndex: firstSuccessfulIndex + 1 + offset)).ToList();
                if (orientationReference is null)
                {
                    throw new InvalidOperationException("Orientation reference frame is not available.");
                }

                var totalBackgroundFrames = filesToProcess.Count;
                var maxParallelism = CalculateFrameLoadParallelism(orientationReference, totalBackgroundFrames);
                using var gate = new SemaphoreSlim(maxParallelism);

                var pending = filesToProcess.Select(entry => Task.Run(async () =>
                {
                    await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    var fileName = Path.GetFileName(entry.File);
                    reporter.NotifyStarted(fileName);

                    try
                    {
                        var raw = await _rustafits.LoadRawFrameAsync(entry.File, CancellationToken.None).ConfigureAwait(false);
                        if (!raw.IsLightFrame)
                        {
                            return (Item: (FrameItem?)null, Frame: (RustafitsService.LoadedFrame?)null, Rotate180: false, ShiftX: 0, ShiftY: 0, Error: (Exception?)null, SourceIndex: entry.SourceIndex, FileName: Path.GetFileName(entry.File));
                        }
                        var rawMetrics = _rustafits.AnalyzeFrame(raw);
                        var orientationReferenceMetrics = Frames[0].Metrics;
                        var orientation = _rustafits.AnalyzeOrientation(raw, rawMetrics, orientationReference, orientationReferenceMetrics);
                        var oriented = _rustafits.ApplyOrientation(raw, orientation.Rotate180);
                        var metrics = _rustafits.ApplyOrientation(rawMetrics, raw.Width, raw.Height, orientation.Rotate180);

                        // Align to the same visual reference as the first loaded frame in the list.
                        // DetectOrientation returns the shift that moves the candidate onto reference,
                        // so we apply that translation directly for rendering.
                        var previewShiftX = orientation.ShiftX;
                        var previewShiftY = orientation.ShiftY;
                        var renderFrame = _isAlignmentEnabled
                            ? _rustafits.ApplyShift(oriented, previewShiftX, previewShiftY)
                            : oriented;
                        var previews = await _rustafits.RenderPreviewBitmapsAsync(renderFrame, GetStfForFrame(renderFrame), _manualRoiRect, metrics, CancellationToken.None).ConfigureAwait(false);

                        var item = new FrameItem
                        {
                            FilePath = entry.File,
                            FileName = Path.GetFileName(entry.File),
                            RelativePath = ComputeRelativePath(entry.File),
                            ExposureDateTime = oriented.ExposureDateTime,
                            ExposureSeconds = oriented.ExposureSeconds,
                            FilterName = oriented.FilterName,
                            ThumbnailImage = previews.Full,
                            RoiImage = previews.Roi,
                            Metrics = metrics,
                            OrientationDebug = orientation.CandidateDebug
                        };

                        return (Item: (FrameItem?)item, Frame: (RustafitsService.LoadedFrame?)oriented, Rotate180: orientation.Rotate180, ShiftX: previewShiftX, ShiftY: previewShiftY, Error: (Exception?)null, SourceIndex: entry.SourceIndex, FileName: item.FileName);
                    }
                    catch (Exception ex)
                    {
                        return (Item: (FrameItem?)null, Frame: (RustafitsService.LoadedFrame?)null, Rotate180: false, ShiftX: 0, ShiftY: 0, Error: (Exception?)ex, SourceIndex: entry.SourceIndex, FileName: Path.GetFileName(entry.File));
                    }
                    finally
                    {
                        gate.Release();
                    }
                })).ToList();

                while (pending.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pending);
                    pending.Remove(completedTask);
                    var result = await completedTask;

                    if (result.Item is not null && result.Frame is not null)
                    {
                        result.Item.PropertyChanged += FrameItem_PropertyChanged;
                        Frames.Add(result.Item);
                        _loadedFrames.Add(CreateLoadedFrameContext(result.Item, result.Frame, result.Item.FilePath, result.Rotate180, result.ShiftX, result.ShiftY));
                        SessionFocalLengthMm ??= result.Frame.FocalLengthMm;
                        SessionPixelSizeUm ??= result.Frame.PixelSizeUm;
                        loadedCount++;
                        reporter.NotifyCompleted(result.Item.FileName);
                    }
                    else
                    {
                        skippedCount++;
                        reporter.NotifySkipped(result.FileName);
                    }

                    // Yield to the dispatcher so WPF can layout/render the newly added row
                    // before the next completion is processed. Without this the listview's
                    // render passes get starved when many completions arrive in rapid bursts.
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            reporter.Stop();
            Status = "Finalizing frame comparisons...";
            UpdateFrameComparisons();
            Status = "Building filter chips...";
            RebuildFilterChips();
            Status = "Initializing rejection thresholds...";
            InitializeThresholdsFromLoadedFrames(preserveExisting: true);
            Status = "Applying rejection thresholds...";
            ApplyThresholds();
            stopwatch.Stop();
            Status = $"Loaded {Frames.Count} frame(s) in {stopwatch.Elapsed.TotalSeconds:F1}s. {skippedCount} skipped.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
        }

        // Start watching after load, including when the folders are currently empty.
        if (WatchFolderEnabled)
            StartFolderWatch();
    }

    private void StartFolderWatch()
    {
        StopFolderWatch();

        if (string.IsNullOrWhiteSpace(InputFolder))
            return;

        var folders = InputFolder.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Where(Directory.Exists)
                                 .ToList();
        if (folders.Count == 0)
            return;

        // Snapshot already-known file paths so we don't re-process them.
        _watcherKnownFiles.Clear();
        foreach (var frame in Frames)
            _watcherKnownFiles.Add(frame.FilePath);

        var watchers = new List<FileSystemWatcher>();
        foreach (var folder in folders)
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            foreach (var ext in new[] { "*.fit", "*.fits", "*.xisf" })
                watcher.Filters.Add(ext);

            watcher.Created += OnWatcherFileCreated;
            watcher.Deleted += OnWatcherFileDeleted;
            watchers.Add(watcher);
        }

        // Store the first watcher for backward-compat; keep all in the dedicated list.
        _folderWatchers = watchers;
        _folderWatcher = watchers[0];
        IsWatchingFolder = true;
    }

    public void StopFolderWatch()
    {
        if (_folderWatchers is null && _folderWatcher is null)
            return;

        var all = _folderWatchers ?? (_folderWatcher is not null ? [_folderWatcher] : []);
        foreach (var w in all)
        {
            w.EnableRaisingEvents = false;
            w.Created -= OnWatcherFileCreated;
            w.Deleted -= OnWatcherFileDeleted;
            w.Dispose();
        }

        _folderWatcher = null;
        _folderWatchers = null;
        _watcherKnownFiles.Clear();
        IsWatchingFolder = false;
    }

    private void OnWatcherFileCreated(object sender, FileSystemEventArgs e)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
            await AddWatchedFileAsync(e.FullPath));
    }

    private void OnWatcherFileDeleted(object sender, FileSystemEventArgs e)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            RemoveWatchedFile(e.FullPath));
    }

    private async Task AddWatchedFileAsync(string filePath)
    {
        if (!_watcherKnownFiles.Add(filePath))
            return;

        // Brief delay: capture programs may still be writing the file.
        await Task.Delay(1500);

        await _watcherAddSemaphore.WaitAsync();
        try
        {
            // Double-check after the delay — could have been added in a racing call.
            if (Frames.Any(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                return;

            RustafitsService.LoadedFrame? raw = null;
            try
            {
                raw = await _rustafits.LoadRawFrameAsync(filePath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Watch: skipped {filePath}: {ex.Message}");
                return;
            }

            if (!raw.IsLightFrame)
                return;

            // Use the first loaded frame as orientation reference if available.
            var reference = _loadedFrames.FirstOrDefault();
            var rotate180 = false;
            var shiftX = 0;
            var shiftY = 0;
            var rawMetrics = _rustafits.AnalyzeFrame(raw);
            var orientation = (Rotate180: false, ShiftX: 0, ShiftY: 0, ReferenceDebug: new OrientationDebugInfo(System.Array.Empty<MeasuredStar>(), System.Array.Empty<int>(), false, "reference", 1.0), CandidateDebug: new OrientationDebugInfo(System.Array.Empty<MeasuredStar>(), System.Array.Empty<int>(), false, "not flipped", 1.0));

            RustafitsService.LoadedFrame oriented;
            AstroMetrics metrics;
            if (reference is not null)
            {
                var referenceMetrics = reference.Item.Metrics;
                orientation = _rustafits.AnalyzeOrientation(raw, rawMetrics, await _rustafits.LoadRawFrameAsync(reference.FilePath, CancellationToken.None), referenceMetrics);
                oriented = _rustafits.ApplyOrientation(raw, orientation.Rotate180);
                metrics = _rustafits.ApplyOrientation(rawMetrics, raw.Width, raw.Height, orientation.Rotate180);
                rotate180 = orientation.Rotate180;
                shiftX = orientation.ShiftX;
                shiftY = orientation.ShiftY;
            }
            else
            {
                oriented = raw;
                metrics = rawMetrics;
            }
            var renderFrame = (_isAlignmentEnabled && reference is not null)
                ? _rustafits.ApplyShift(oriented, shiftX, shiftY)
                : oriented;
            var previews = await _rustafits.RenderPreviewBitmapsAsync(renderFrame, GetStfForFrame(renderFrame), _manualRoiRect, metrics, CancellationToken.None);

            var item = new FrameItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                RelativePath = ComputeRelativePath(filePath),
                ExposureDateTime = oriented.ExposureDateTime,
                ExposureSeconds = oriented.ExposureSeconds,
                FilterName = oriented.FilterName,
                ThumbnailImage = previews.Full,
                RoiImage = previews.Roi,
                Metrics = metrics,
                OrientationDebug = reference is not null ? orientation.CandidateDebug : _rustafits.CreateOrientationReferenceDebugInfo(oriented, metrics)
            };

            var wasEmpty = Frames.Count == 0;

            item.PropertyChanged += FrameItem_PropertyChanged;
            Frames.Add(item);
            _loadedFrames.Add(CreateLoadedFrameContext(item, oriented, filePath, rotate180, shiftX, shiftY));
            SessionFocalLengthMm ??= oriented.FocalLengthMm;
            SessionPixelSizeUm ??= oriented.PixelSizeUm;

            if (wasEmpty)
            {
                var autoStf = _rustafits.ComputeAutoStretch(oriented, _stfTargetBackground);
                _stfShadows = autoStf.Shadows;
                _stfMidtones = autoStf.Midtones;
                _stfHighlights = autoStf.Highlights;
                OnPropertyChanged(nameof(StfShadows));
                OnPropertyChanged(nameof(StfMidtones));
                OnPropertyChanged(nameof(StfHighlights));
                _manualRoiRect = _rustafits.DetectRoiNormalizedRect(oriented);
                InitializeThresholdsFromLoadedFrames();
            }

            UpdateFrameComparisons();
            RebuildFilterChips();
            ApplyThresholds();

            Status = $"Watch: added {item.FileName} — {Frames.Count} frame(s) total.";
        }
        finally
        {
            _watcherAddSemaphore.Release();
        }
    }

    private void RemoveWatchedFile(string filePath)
    {
        _watcherKnownFiles.Remove(filePath);

        var item = Frames.FirstOrDefault(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.PropertyChanged -= FrameItem_PropertyChanged;
        Frames.Remove(item);

        var idx = _loadedFrames.FindIndex(f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _loadedFrames.RemoveAt(idx);
        }

        if (SelectedFrame is not null && ReferenceEquals(SelectedFrame, item))
        {
            SelectedFrame = Frames.FirstOrDefault();
        }

        UpdateFrameComparisons();
        RebuildFilterChips();
        ApplyThresholds();
        ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(MoveRejectedEnabled));

        Status = $"Watch: removed {item.FileName} — {Frames.Count} frame(s) total.";
    }

    private void SaveSession()
    {
        using var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save Session",
            Filter = "Rejector Session (*.boms)|*.boms|All files (*.*)|*.*",
            DefaultExt = "boms",
            FileName = "session.boms"
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            var session = new SessionData
            {
                SavedAt = DateTimeOffset.Now,
                InputFolder = InputFolder,
                RejectedFolder = RejectedFolder,
                IncludeSubfolders = IncludeSubfolders,
                MaxFwhm = MaxFwhm,
                MaxFwhmArcsec = MaxFwhmArcsec,
                MaxHfr = MaxHfr,
                MaxEccentricity = MaxEccentricity,
                MaxMeanBackground = MaxMeanBackground,
                MinStars = MinStars,
                MinSqm = MinSqm,
                MaxSkyTemp = MaxSkyTemp,
                MinSatelliteConfidence = MinSatelliteConfidence,
                RejectSatelliteTrail = RejectSatelliteTrail,
                MinScore = MinScore,
                StfShadows = StfShadows,
                StfMidtones = StfMidtones,
                StfHighlights = StfHighlights,
                StfTargetBackground = StfTargetBackground,
                AutoStretchPerFrame = AutoStretchPerFrame,
                ShowAccepted = ShowAccepted,
                ShowRejected = ShowRejected,
                ManualRoi = _manualRoiRect is { } roi
                    ? new SessionRoiRect { Left = roi.Left, Top = roi.Top, Width = roi.Width, Height = roi.Height }
                    : null,
                SortRules = SortRules
                    .Select(r => new SessionSortRule { Field = r.SelectedField.Value.ToString(), Direction = r.SelectedDirection.Value.ToString() })
                    .ToList(),
                FilterChips = FilterChips
                    .Select(c => new SessionFilterChip { Key = c.Key, IsSelected = c.IsSelected })
                    .ToList(),
                FilterThresholds = _filterThresholds
                    .Select(kvp => new SessionFilterThresholds
                    {
                        Key = kvp.Key,
                        MaxFwhm = kvp.Value.MaxFwhm,
                        MaxFwhmArcsec = kvp.Value.MaxFwhmArcsec,
                        MaxHfr = kvp.Value.MaxHfr,
                        MaxEccentricity = kvp.Value.MaxEccentricity,
                        MaxMeanBackground = kvp.Value.MaxMeanBackground,
                        MinStars = kvp.Value.MinStars,
                        MinSqm = kvp.Value.MinSqm,
                        MaxSkyTemp = kvp.Value.MaxSkyTemp,
                        MinSatelliteConfidence = kvp.Value.MinSatelliteConfidence,
                        MinScore = kvp.Value.MinScore,
                        AutoCalcTrailThreshold = kvp.Value.AutoCalcTrailThreshold,
                        AutoCalcFwhmThreshold = kvp.Value.AutoCalcFwhmThreshold,
                        AutoCalcFwhmArcsecThreshold = kvp.Value.AutoCalcFwhmArcsecThreshold,
                        AutoCalcSqmThreshold = kvp.Value.AutoCalcSqmThreshold,
                        AutoCalcSkyTempThreshold = kvp.Value.AutoCalcSkyTempThreshold,
                        AutoCalcHfrThreshold = kvp.Value.AutoCalcHfrThreshold,
                        AutoCalcEccentricityThreshold = kvp.Value.AutoCalcEccentricityThreshold,
                        AutoCalcMeanBackgroundThreshold = kvp.Value.AutoCalcMeanBackgroundThreshold,
                        AutoCalcStarsThreshold = kvp.Value.AutoCalcStarsThreshold,
                        AutoCalcScoreThreshold = kvp.Value.AutoCalcScoreThreshold,
                    })
                    .ToList(),
                Frames = _loadedFrames
                    .Select(ctx => new SessionFrameEntry
                    {
                        FilePath = ctx.FilePath,
                        FileName = ctx.Item.FileName,
                        RelativePath = ctx.Item.RelativePath,
                        AutoRejected = ctx.Item.AutomaticRejected,
                        ManualRejectedOverride = ctx.Item.ManualRejectedOverride,
                        OverallScore = ctx.Item.OverallScore,
                        Fwhm = ctx.Item.Metrics.Fwhm,
                        FwhmArcsec = ctx.Item.Metrics.FwhmArcsec,
                        Sqm = ctx.Item.Metrics.Sqm,
                        SkyTemp = ctx.Item.Metrics.SkyTemp,
                        Hfr = ctx.Item.Metrics.Hfr,
                        StarCount = ctx.Item.Metrics.StarCount,
                        Eccentricity = ctx.Item.Metrics.Eccentricity,
                        MeanBackground = ctx.Item.Metrics.MeanBackground,
                        Median = ctx.Item.Metrics.Median,
                        Mad = ctx.Item.Metrics.Mad,
                        Min = ctx.Item.Metrics.Min,
                        MinCount = ctx.Item.Metrics.MinCount,
                        Max = ctx.Item.Metrics.Max,
                        MaxCount = ctx.Item.Metrics.MaxCount,
                        SatelliteTrailConfidence = ctx.Item.Metrics.SatelliteTrailConfidence,
                        TrailX1 = ctx.Item.Metrics.TrailX1,
                        TrailY1 = ctx.Item.Metrics.TrailY1,
                        TrailX2 = ctx.Item.Metrics.TrailX2,
                        TrailY2 = ctx.Item.Metrics.TrailY2,
                        ExposureDateTime = ctx.ExposureDateTime,
                        ExposureSeconds = ctx.ExposureSeconds,
                        FilterName = ctx.FilterName,
                        FocalLengthMm = ctx.FocalLengthMm,
                        PixelSizeUm = ctx.PixelSizeUm,
                        Width = ctx.Width,
                        Height = ctx.Height,
                        Rotate180 = ctx.Rotate180,
                        ShiftX = ctx.ShiftX,
                        ShiftY = ctx.ShiftY,
                        NormalizationMax = ctx.NormalizationMax,
                        ThumbnailPng = SessionService.EncodeBitmap(ctx.Item.ThumbnailImage),
                        RoiPng = SessionService.EncodeBitmap(ctx.Item.RoiImage)
                    })
                    .ToList()
            };

            _session.Save(dialog.FileName, session);
            Status = $"Session saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Status = $"Save session failed: {ex.Message}";
        }
    }

    private async Task LoadSessionAsync()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Load Session",
            Filter = "Rejector Session (*.boms)|*.boms|All files (*.*)|*.*",
            DefaultExt = "boms"
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        SessionData session;
        try
        {
            var loaded = _session.Load(dialog.FileName);
            if (loaded is null)
            {
                Status = "Failed to load session: invalid file.";
                return;
            }

            session = loaded;
        }
        catch (Exception ex)
        {
            Status = $"Load session failed: {ex.Message}";
            return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressValue = 0;
        Status = "Restoring session...";

        foreach (var frame in Frames)
        {
            frame.PropertyChanged -= FrameItem_PropertyChanged;
        }

        Frames.Clear();
        foreach (var chip in FilterChips)
        {
            chip.PropertyChanged -= FilterChip_PropertyChanged;
        }

        FilterChips.Clear();
        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasMultipleFilterChips));
        _loadedFrames.Clear();
        ResetFrameStatistics();
        SelectedFrame = null;
        _manualRoiRect = null;
        _hasManualRoi = false;
        SessionFocalLengthMm = null;
        SessionPixelSizeUm = null;

        try
        {
            // Restore global settings without triggering threshold recalculation mid-restore
            _inputFolder = session.InputFolder;
            OnPropertyChanged(nameof(InputFolder));
            _rejectedFolder = session.RejectedFolder;
            OnPropertyChanged(nameof(RejectedFolder));
            _includeSubfolders = session.IncludeSubfolders;
            OnPropertyChanged(nameof(IncludeSubfolders));
            _showAccepted = session.ShowAccepted;
            OnPropertyChanged(nameof(ShowAccepted));
            _showRejected = session.ShowRejected;
            OnPropertyChanged(nameof(ShowRejected));

            // Restore per-filter thresholds (with legacy single-threshold fallback).
            _filterThresholds.Clear();
            if (session.FilterThresholds.Count > 0)
            {
                foreach (var ft in session.FilterThresholds)
                {
                    var thresholdKey = NormalizeFilterValue(ft.Key);
                    _filterThresholds[thresholdKey] = new Thresholds
                    {
                        MaxFwhm = ft.MaxFwhm,
                        MaxFwhmArcsec = ft.MaxFwhmArcsec > 0 ? ft.MaxFwhmArcsec : 20.0,
                        MaxHfr = ft.MaxHfr,
                        MaxEccentricity = ft.MaxEccentricity,
                        MaxMeanBackground = ft.MaxMeanBackground,
                        MinStars = ft.MinStars,
                        MinSqm = ft.MinSqm,
                        MaxSkyTemp = ft.MaxSkyTemp,
                        MinSatelliteConfidence = ft.MinSatelliteConfidence,
                        MinScore = ft.MinScore,
                        AutoCalcTrailThreshold = ft.AutoCalcTrailThreshold,
                        AutoCalcFwhmThreshold = ft.AutoCalcFwhmThreshold,
                        AutoCalcFwhmArcsecThreshold = ft.AutoCalcFwhmArcsecThreshold,
                        AutoCalcSqmThreshold = ft.AutoCalcSqmThreshold,
                        AutoCalcSkyTempThreshold = ft.AutoCalcSkyTempThreshold,
                        AutoCalcHfrThreshold = ft.AutoCalcHfrThreshold,
                        AutoCalcEccentricityThreshold = ft.AutoCalcEccentricityThreshold,
                        AutoCalcMeanBackgroundThreshold = ft.AutoCalcMeanBackgroundThreshold,
                        AutoCalcStarsThreshold = ft.AutoCalcStarsThreshold,
                        AutoCalcScoreThreshold = ft.AutoCalcScoreThreshold,
                    };
                }

                if (!_filterThresholds.ContainsKey(string.Empty) && _filterThresholds.Count > 0)
                {
                    _filterThresholds[string.Empty] = CloneThresholds(_filterThresholds.Values.First());
                }
            }
            else
            {
                // Legacy sessions stored a single global threshold set; apply it to the "" key as a starting point.
                _filterThresholds[string.Empty] = new Thresholds
                {
                    MaxFwhm = session.MaxFwhm,
                    MaxFwhmArcsec = session.MaxFwhmArcsec > 0 ? session.MaxFwhmArcsec : 20.0,
                    MaxHfr = session.MaxHfr,
                    MaxEccentricity = session.MaxEccentricity,
                    MaxMeanBackground = session.MaxMeanBackground,
                    MinStars = session.MinStars,
                    MinSqm = session.MinSqm,
                    MaxSkyTemp = session.MaxSkyTemp,
                    MinSatelliteConfidence = session.MinSatelliteConfidence,
                    MinScore = session.MinScore,
                    AutoCalcTrailThreshold = true,
                    AutoCalcFwhmThreshold = true,
                    AutoCalcFwhmArcsecThreshold = true,
                    AutoCalcSqmThreshold = true,
                    AutoCalcSkyTempThreshold = true,
                    AutoCalcHfrThreshold = true,
                    AutoCalcEccentricityThreshold = true,
                    AutoCalcMeanBackgroundThreshold = true,
                    AutoCalcStarsThreshold = true,
                    AutoCalcScoreThreshold = true,
                };
            }
            _rejectSatelliteTrail = session.RejectSatelliteTrail;
            OnPropertyChanged(nameof(RejectSatelliteTrail));
            RaiseAllThresholdPropertiesChanged();
            _stfShadows = session.StfShadows;
            OnPropertyChanged(nameof(StfShadows));
            _stfMidtones = session.StfMidtones;
            OnPropertyChanged(nameof(StfMidtones));
            _stfHighlights = session.StfHighlights;
            OnPropertyChanged(nameof(StfHighlights));
            _stfTargetBackground = session.StfTargetBackground;
            OnPropertyChanged(nameof(StfTargetBackground));
            _autoStretchPerFrame = session.AutoStretchPerFrame;
            OnPropertyChanged(nameof(AutoStretchPerFrame));

            if (session.ManualRoi is { } roi)
            {
                _manualRoiRect = (roi.Left, roi.Top, roi.Width, roi.Height);
                _hasManualRoi = true;
            }

            // Restore sort rules
            if (session.SortRules.Count > 0)
            {
                foreach (var rule in SortRules)
                {
                    rule.PropertyChanged -= SortRule_PropertyChanged;
                }

                SortRules.Clear();

                foreach (var savedRule in session.SortRules)
                {
                    if (Enum.TryParse<FrameSortField>(savedRule.Field, out var sortField) &&
                        Enum.TryParse<ListSortDirection>(savedRule.Direction, out var sortDir))
                    {
                        var fieldOption = SortFieldOptions.FirstOrDefault(o => o.Value == sortField) ?? SortFieldOptions[0];
                        var dirOption = SortDirectionOptions.FirstOrDefault(o => o.Value == sortDir) ?? SortDirectionOptions[0];
                        AddSortRule(fieldOption, dirOption);
                    }
                }

                if (SortRules.Count == 0)
                {
                    AddSortRule(DefaultPrimarySortField, SortDirectionOptions[0]);
                }
            }

            // Restore frames from session cache
            ProgressMaximum = Math.Max(1, session.Frames.Count);
            var restoredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < session.Frames.Count; i++)
            {
                var entry = session.Frames[i];
                Status = $"Restoring frame {i + 1}/{session.Frames.Count}: {entry.FileName}";

                var metrics = new AstroMetrics
                {
                    Fwhm = entry.Fwhm,
                    FwhmArcsec = entry.FwhmArcsec,
                    Sqm = entry.Sqm,
                    SkyTemp = entry.SkyTemp,
                    Hfr = entry.Hfr,
                    StarCount = entry.StarCount,
                    Eccentricity = entry.Eccentricity,
                    MeanBackground = entry.MeanBackground,
                    Median = entry.Median,
                    Mad = entry.Mad,
                    Min = entry.Min,
                    MinCount = entry.MinCount,
                    Max = entry.Max,
                    MaxCount = entry.MaxCount,
                    SatelliteTrailConfidence = entry.SatelliteTrailConfidence,
                    TrailX1 = entry.TrailX1,
                    TrailY1 = entry.TrailY1,
                    TrailX2 = entry.TrailX2,
                    TrailY2 = entry.TrailY2
                };

                var thumbnail = await Task.Run(() => SessionService.DecodeBitmap(entry.ThumbnailPng));
                var roiImage = await Task.Run(() => SessionService.DecodeBitmap(entry.RoiPng));

                var item = new FrameItem
                {
                    FilePath = entry.FilePath,
                    FileName = entry.FileName,
                    RelativePath = entry.RelativePath,
                    ExposureDateTime = entry.ExposureDateTime,
                    ExposureSeconds = entry.ExposureSeconds,
                    FilterName = entry.FilterName,
                    ThumbnailImage = thumbnail,
                    RoiImage = roiImage,
                    Metrics = metrics
                };

                item.SetAutomaticRejected(entry.AutoRejected);
                item.SetManualRejectedOverride(entry.ManualRejectedOverride);
                item.OverallScore = entry.OverallScore;
                item.PropertyChanged += FrameItem_PropertyChanged;
                Frames.Add(item);

                _loadedFrames.Add(new LoadedFrameContext(
                    item,
                    entry.FilePath,
                    entry.Width,
                    entry.Height,
                    entry.NormalizationMax,
                    entry.Rotate180,
                    entry.ShiftX,
                    entry.ShiftY,
                    entry.FocalLengthMm,
                    entry.PixelSizeUm,
                    entry.ExposureDateTime,
                    entry.ExposureSeconds,
                    entry.FilterName,
                    entry.Sqm,
                    entry.SkyTemp,
                    null));

                restoredSet.Add(entry.FilePath);
                SessionFocalLengthMm ??= entry.FocalLengthMm;
                SessionPixelSizeUm ??= entry.PixelSizeUm;
                ProgressValue = i + 1;
            }

            // Scan folder for new files not in the session
            if (!string.IsNullOrWhiteSpace(session.InputFolder) && Directory.Exists(session.InputFolder))
            {
                var allFiles = _discovery.Discover(session.InputFolder, session.IncludeSubfolders);
                var newFiles = allFiles.Where(f => !restoredSet.Contains(f)).ToList();

                if (newFiles.Count > 0)
                {
                    Status = $"Found {newFiles.Count} new file(s) not in session. Loading...";
                    ProgressValue = 0;
                    ProgressMaximum = Math.Max(1, newFiles.Count);
                    var newSkipped = 0;
                    var newLoaded = 0;
                    RustafitsService.LoadedFrame? orientationReference = _loadedFrames.Count > 0
                        ? await MaterializeFrameAsync(_loadedFrames[0], CancellationToken.None)
                        : null;

                    for (var i = 0; i < newFiles.Count; i++)
                    {
                        var file = newFiles[i];
                        Status = $"Loading new frame {i + 1}/{newFiles.Count}: {Path.GetFileName(file)}";

                        try
                        {
                            var raw = await _rustafits.LoadRawFrameAsync(file, CancellationToken.None);
                            var rawMetrics = _rustafits.AnalyzeFrame(raw);
                            var orientation = orientationReference is not null
                                ? _rustafits.AnalyzeOrientation(raw, rawMetrics, orientationReference, _loadedFrames[0].Item.Metrics)
                                : (Rotate180: false, ShiftX: 0, ShiftY: 0, ReferenceDebug: new OrientationDebugInfo(System.Array.Empty<MeasuredStar>(), System.Array.Empty<int>(), false, "reference", 1.0), CandidateDebug: new OrientationDebugInfo(System.Array.Empty<MeasuredStar>(), System.Array.Empty<int>(), false, "not flipped", 1.0));
                            var oriented = _rustafits.ApplyOrientation(raw, orientation.Rotate180);
                            var metrics = _rustafits.ApplyOrientation(rawMetrics, raw.Width, raw.Height, orientation.Rotate180);
                            var previewShiftX = orientation.ShiftX;
                            var previewShiftY = orientation.ShiftY;
                            var renderFrame = _isAlignmentEnabled
                                ? _rustafits.ApplyShift(oriented, previewShiftX, previewShiftY)
                                : oriented;
                            var previews = await _rustafits.RenderPreviewBitmapsAsync(renderFrame, GetStfForFrame(renderFrame), _manualRoiRect, metrics, CancellationToken.None);

                            var newItem = new FrameItem
                            {
                                FilePath = file,
                                FileName = Path.GetFileName(file),
                                RelativePath = ComputeRelativePath(file),
                                ExposureDateTime = oriented.ExposureDateTime,
                                ExposureSeconds = oriented.ExposureSeconds,
                                FilterName = oriented.FilterName,
                                ThumbnailImage = previews.Full,
                                RoiImage = previews.Roi,
                                Metrics = metrics,
                                OrientationDebug = orientation.CandidateDebug
                            };

                            newItem.PropertyChanged += FrameItem_PropertyChanged;
                            Frames.Add(newItem);
                            _loadedFrames.Add(CreateLoadedFrameContext(newItem, oriented, file, orientation.Rotate180, previewShiftX, previewShiftY));
                            SessionFocalLengthMm ??= oriented.FocalLengthMm;
                            SessionPixelSizeUm ??= oriented.PixelSizeUm;
                            orientationReference ??= oriented;
                            newLoaded++;
                        }
                        catch (Exception ex)
                        {
                            newSkipped++;
                            Status = $"Skipped new file {Path.GetFileName(file)}: {ex.Message}";
                        }

                        ProgressValue = i + 1;
                    }

                    if (newLoaded > 0)
                    {
                        UpdateFrameComparisons();
                        InitializeThresholdsFromLoadedFrames(preserveExisting: true);
                        ApplyThresholds();
                    }

                    Status = $"Session restored with {session.Frames.Count} saved frame(s) + {newLoaded} new frame(s). {newSkipped} skipped.";
                }
                else
                {
                    Status = $"Session restored: {session.Frames.Count} frame(s).";
                }
            }
            else
            {
                Status = $"Session restored: {session.Frames.Count} frame(s).";
            }

            // Rebuild chips restoring selection state from session
            RebuildFilterChips();
            if (session.FilterChips.Count > 0)
            {
                _isUpdatingFilterSelection = true;
                try
                {
                    foreach (var chip in FilterChips)
                    {
                        var saved = session.FilterChips.FirstOrDefault(c => string.Equals(c.Key, chip.Key, StringComparison.OrdinalIgnoreCase));
                        if (saved is not null)
                        {
                            chip.IsSelected = saved.IsSelected;
                        }
                    }
                }
                finally
                {
                    _isUpdatingFilterSelection = false;
                }
            }

            FilteredFrames.Refresh();
            UpdateFrameStatistics();
            ApplySorting();
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SaveSessionCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Status = $"Error restoring session: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
        }
    }

    private void UpdateFrameComparisons()
    {
        if (Frames.Count == 0)
        {
            return;
        }

        var green = WpfBrushes.LimeGreen;
        var yellow = WpfBrushes.Goldenrod;
        var red = WpfBrushes.IndianRed;

        // FWHM, HFR and Eccentricity reflect seeing / optics / tracking and are
        // independent of filter — compare them globally across the whole session.
        var avgFwhm = Frames.Average(f => f.Metrics.Fwhm);
        var avgHfr  = Frames.Average(f => f.Metrics.Hfr);
        var avgEcc  = Frames.Average(f => f.Metrics.Eccentricity);

        // Stars and Mean Background vary by filter (L detects far more stars than RGB),
        // so compare each frame against its own filter peers only.
        var groups = Frames
            .GroupBy(f => NormalizeFilterValue(f.FilterName), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var members = group.ToList();
            var avgStars = members.Average(f => (double)f.Metrics.StarCount);
            var avgBg    = members.Average(f => f.Metrics.MeanBackground);

            foreach (var frame in members)
            {
                frame.FwhmIndicatorBrush            = CompareLowerIsBetter(frame.Metrics.Fwhm, avgFwhm, green, yellow, red);
                frame.HfrIndicatorBrush             = CompareLowerIsBetter(frame.Metrics.Hfr, avgHfr, green, yellow, red);
                frame.StarsIndicatorBrush           = CompareHigherIsBetter(frame.Metrics.StarCount, avgStars, green, yellow, red);
                frame.EccentricityIndicatorBrush    = CompareLowerIsBetter(frame.Metrics.Eccentricity, avgEcc, green, yellow, red);
                frame.MeanBackgroundIndicatorBrush  = CompareLowerIsBetter(frame.Metrics.MeanBackground, avgBg, green, yellow, red);
                frame.TrailIndicatorBrush           = frame.Metrics.SatelliteTrailConfidence >= 60 ? red : green;

                // Score is computed below using rank-percentile logic (see ComputePercentileScores)
            }
        }

        ComputePercentileScores();
    }

    /// <summary>
    /// Scores each frame on a 0–5 scale using weighted rank-percentile scoring.
    /// Each metric is ranked across all frames; the rank is converted to a [0,1]
    /// percentile. Weighted percentiles are combined and scaled to 0–5.
    /// This guarantees the best frame in the session always scores near 5.0 and
    /// the distribution spans the full range, making it actually useful for culling.
    ///
    /// Weights reflect typical astro importance by default and can be customized in Settings.
    /// </summary>
    private void ComputePercentileScores()
    {
        if (Frames.Count == 0) return;

        var fwhmWeight  = ShowFwhmMetric && UseScoreFwhm ? Math.Max(0.0, ScoreWeightFwhm) : 0.0;
        var eccWeight   = ShowEccentricityMetric && UseScoreEccentricity ? Math.Max(0.0, ScoreWeightEccentricity) : 0.0;
        var hfrWeight   = ShowHfrMetric && UseScoreHfr ? Math.Max(0.0, ScoreWeightHfr) : 0.0;
        var starsWeight = ShowStarsMetric && UseScoreStars ? Math.Max(0.0, ScoreWeightStars) : 0.0;
        var bgWeight    = ShowMeanBackgroundMetric && UseScoreMeanBackground ? Math.Max(0.0, ScoreWeightMeanBackground) : 0.0;
        var trailWeight = ShowTrailMetric && UseScoreTrail ? Math.Max(0.0, ScoreWeightTrail) : 0.0;
        var totalWeight = fwhmWeight + eccWeight + hfrWeight + starsWeight + bgWeight + trailWeight;

        static double[] RankPercentile(double[] values, bool lowerIsBetter)
        {
            var n = values.Length;
            if (n == 0) return [];
            if (n == 1) return [1.0];

            // Missing / invalid values are considered worst and therefore rank at 0.
            var percentiles = new double[n];
            var indexed = values
                .Select((v, i) => (v, i))
                .Where(x => double.IsFinite(x.v))
                .OrderBy(x => x.v)
                .ToArray();

            if (indexed.Length == 0)
            {
                return percentiles;
            }

            if (indexed.Length == 1)
            {
                percentiles[indexed[0].i] = 1.0;
                return percentiles;
            }

            var rank = 0;
            while (rank < indexed.Length)
            {
                var val = indexed[rank].v;
                var tieEnd = rank;
                while (tieEnd + 1 < indexed.Length && indexed[tieEnd + 1].v == val) tieEnd++;

                var avgRank = (rank + tieEnd) / 2.0;
                var normalized = avgRank / (indexed.Length - 1.0);
                var pct = lowerIsBetter ? (1.0 - normalized) : normalized;

                for (var t = rank; t <= tieEnd; t++)
                {
                    percentiles[indexed[t].i] = pct;
                }

                rank = tieEnd + 1;
            }

            return percentiles;
        }

        // Score per filter group so each frame is ranked against its peers (same filter).
        var groups = Frames
            .Select((f, idx) => (Frame: f, Idx: idx, Key: NormalizeFilterValue(f.FilterName)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var members = group.ToArray();
            var fwhmPct  = RankPercentile(members.Select(m => m.Frame.Metrics.Fwhm).ToArray(),              lowerIsBetter: true);
            var eccPct   = RankPercentile(members.Select(m => m.Frame.Metrics.Eccentricity).ToArray(),       lowerIsBetter: true);
            var hfrPct   = RankPercentile(members.Select(m => m.Frame.Metrics.Hfr).ToArray(),                lowerIsBetter: true);
            var starsPct = RankPercentile(members.Select(m => (double)m.Frame.Metrics.StarCount).ToArray(),  lowerIsBetter: false);
            var bgPct    = RankPercentile(members.Select(m => m.Frame.Metrics.MeanBackground).ToArray(),     lowerIsBetter: true);
            var trailPct = RankPercentile(members.Select(m => (double)m.Frame.Metrics.SatelliteTrailConfidence).ToArray(), lowerIsBetter: true);

            for (var i = 0; i < members.Length; i++)
            {
                if (totalWeight <= double.Epsilon)
                {
                    members[i].Frame.OverallScore = 0.0;
                    continue;
                }

                var weighted = fwhmPct[i]  * fwhmWeight
                             + eccPct[i]   * eccWeight
                             + hfrPct[i]   * hfrWeight
                             + starsPct[i] * starsWeight
                             + bgPct[i]    * bgWeight
                             + trailPct[i] * trailWeight;

                members[i].Frame.OverallScore = Math.Clamp((weighted / totalWeight) * 5.0, 0.0, 5.0);
            }
        }
    }

    private static WpfBrush CompareLowerIsBetter(double value, double average, WpfBrush green, WpfBrush yellow, WpfBrush red)
    {
        if (value <= average * 0.92)
        {
            return green;
        }

        if (value >= average * 1.08)
        {
            return red;
        }

        return yellow;
    }

    private static WpfBrush CompareHigherIsBetter(double value, double average, WpfBrush green, WpfBrush yellow, WpfBrush red)
    {
        if (value >= average * 1.08)
        {
            return green;
        }

        if (value <= average * 0.92)
        {
            return red;
        }

        return yellow;
    }

    private void InitializeThresholdsFromLoadedFrames(bool preserveExisting = false)
    {
        if (Frames.Count == 0)
        {
            return;
        }

        // Initialize per-filter thresholds to "everything passes" defaults (max of each metric within that group).
        var groups = Frames
            .GroupBy(f => NormalizeFilterValue(f.FilterName), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var frames = group.ToList();
            if (frames.Count == 0) continue;

            if (preserveExisting && _filterThresholds.ContainsKey(group.Key))
            {
                continue;
            }

            var t = GetThresholdsForKey(group.Key);
            if (t.AutoCalcFwhmThreshold)
                t.MaxFwhm = frames.Max(f => f.Metrics.Fwhm);
            var fwhmArcsecValues = frames.Where(f => f.Metrics.FwhmArcsec.HasValue).Select(f => f.Metrics.FwhmArcsec!.Value).ToList();
            if (t.AutoCalcFwhmArcsecThreshold)
                t.MaxFwhmArcsec = fwhmArcsecValues.Count > 0 ? fwhmArcsecValues.Max() : 20.0;
            if (t.AutoCalcHfrThreshold)
                t.MaxHfr = frames.Max(f => f.Metrics.Hfr);
            if (t.AutoCalcEccentricityThreshold)
                t.MaxEccentricity = frames.Max(f => f.Metrics.Eccentricity);
            if (t.AutoCalcMeanBackgroundThreshold)
                t.MaxMeanBackground = frames.Max(f => f.Metrics.MeanBackground);
            if (t.AutoCalcStarsThreshold)
                t.MinStars = frames.Min(f => (double)f.Metrics.StarCount);

            var sqm = frames.Where(f => f.Metrics.Sqm.HasValue).Select(f => f.Metrics.Sqm!.Value).ToList();
            if (t.AutoCalcSqmThreshold)
                t.MinSqm = sqm.Count > 0 ? sqm.Min() : 0.0;

            var skyTemp = frames.Where(f => f.Metrics.SkyTemp.HasValue).Select(f => f.Metrics.SkyTemp!.Value).ToList();
            if (t.AutoCalcSkyTempThreshold)
                t.MaxSkyTemp = skyTemp.Count > 0 ? skyTemp.Max() : 40.0;
            if (t.AutoCalcTrailThreshold)
                t.MinSatelliteConfidence = 80;
            if (t.AutoCalcScoreThreshold)
                t.MinScore = 0.0;
        }

        RaiseAllThresholdPropertiesChanged();
    }

    private void ScheduleThumbnailRebuild(bool immediate = false)
    {
        if (_previewWindow is not null)
        {
            _stretchRefreshCts?.Cancel();
            _stretchRefreshCts?.Dispose();
            _stretchRefreshCts = null;
            _thumbnailRefreshPendingWhilePreviewOpen = true;
            return;
        }

        _stretchRefreshCts?.Cancel();
        _stretchRefreshCts?.Dispose();

        var cts = new CancellationTokenSource();
        _stretchRefreshCts = cts;
        _ = RebuildThumbnailsDeferredAsync(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(StretchRefreshDebounceMs), cts.Token);
    }

    private void OnStretchSettingsChanged()
    {
        InvalidateFullImageCaches();

        _stretchRefreshCts?.Cancel();
        _stretchRefreshCts?.Dispose();

        var cts = new CancellationTokenSource();
        _stretchRefreshCts = cts;
        _ = ApplyStretchSettingsAsync(cts.Token);
    }

    private async Task ApplyStretchSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshActivePreviewInteractiveAsync(cancellationToken);

            if (_previewWindow is not null)
            {
                _thumbnailRefreshPendingWhilePreviewOpen = true;
                var idleDelay = _isInteractiveStretchActive
                    ? PreviewFullResolutionIdleMs * 2
                    : PreviewFullResolutionIdleMs;
                await Task.Delay(TimeSpan.FromMilliseconds(idleDelay), cancellationToken);
                await RefreshActivePreviewFullResolutionAsync(cancellationToken);
                return;
            }

            await RebuildThumbnailsDeferredAsync(TimeSpan.FromMilliseconds(StretchRefreshDebounceMs), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BeginInteractiveStretch()
    {
        _isInteractiveStretchActive = true;
    }

    private void EndInteractiveStretch()
    {
        _isInteractiveStretchActive = false;
        OnStretchSettingsChanged();
    }

    private void SetAlignmentEnabled(bool enabled)
    {
        if (_isAlignmentEnabled == enabled)
        {
            return;
        }

        _isAlignmentEnabled = enabled;
        // Drop any cached materialized frame so the next render uses the new alignment state.
        InvalidateInteractiveRawFrame();
        // Drop any cached full-resolution preview bitmaps. They were rendered under the previous
        // alignment state, so without clearing them the preview window briefly shows the stale
        // unaligned (or previously aligned) bitmap before the new render completes.
        ClearAllFullImageCaches();
        OnPropertyChanged(nameof(IsAlignmentEnabled));
        // Mirror the change into the preview view-model (if open) so its Align chip stays in sync
        // when the toggle is flipped from the main window.
        _previewVm?.NotifyAlignmentChanged();
        // Refresh the active preview canvas immediately and rebuild list thumbnails / ROI in the
        // background so the rest of the UI also reflects the new alignment state.
        _ = RefreshActivePreviewFullResolutionAsync(CancellationToken.None);
        _ = RebuildThumbnailsDeferredAsync(TimeSpan.Zero, CancellationToken.None);
    }

    public bool IsAlignmentEnabled
    {
        get => _isAlignmentEnabled;
        set => SetAlignmentEnabled(value);
    }

    private async Task ApplyAutoStretchAsync()
    {
        if (_loadedFrames.Count == 0) return;

        var targetItem = _previewItem ?? SelectedFrame ?? _loadedFrames[0].Item;
        var targetIndex = _loadedFrames.FindIndex(f => f.Item == targetItem);
        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        var stf = _rustafits.ComputeAutoStretch(await MaterializeFrameAsync(_loadedFrames[targetIndex], CancellationToken.None), _stfTargetBackground);
        _stfShadows = stf.Shadows;
        _stfMidtones = stf.Midtones;
        _stfHighlights = stf.Highlights;
        OnPropertyChanged(nameof(StfShadows));
        OnPropertyChanged(nameof(StfMidtones));
        OnPropertyChanged(nameof(StfHighlights));
        InvalidateFullImageCaches();
        OnStretchSettingsChanged();
    }

    private void InvalidateFullImageCaches()
    {
        var anyChanged = false;
        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            if (_loadedFrames[i].FullImage is null)
            {
                continue;
            }

            _loadedFrames[i] = _loadedFrames[i] with { FullImage = null };
            anyChanged = true;
        }

        if (anyChanged)
        {
            PublishPreviewCacheState();
        }
    }

    private async Task RefreshActivePreviewInteractiveAsync(CancellationToken cancellationToken)
    {
        if (_previewItem is null || _previewWindow is null)
        {
            return;
        }

        try
        {
            await _previewRefreshSemaphore.WaitAsync(cancellationToken);

            var activeItem = _previewItem;
            var previewWindow = _previewWindow;
            if (activeItem is null || previewWindow is null)
            {
                return;
            }

            var index = _loadedFrames.FindIndex(f => f.Item == activeItem);
            if (index < 0)
            {
                return;
            }

            var loaded = _loadedFrames[index];
            var (targetWidth, targetHeight) = GetInteractivePreviewDimensions(loaded, _isInteractiveStretchActive);
            var materialized = await GetOrCreateInteractiveRawFrameAsync(loaded, cancellationToken);
            var previewImage = await _rustafits.RenderScaledPreviewBitmapAsync(materialized, targetWidth, targetHeight, GetStfForFrame(materialized), cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !ReferenceEquals(_previewWindow, previewWindow) ||
                !ReferenceEquals(_previewItem, activeItem))
            {
                return;
            }

            previewWindow.RefreshImage(previewImage);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_previewRefreshSemaphore.CurrentCount == 0)
            {
                _previewRefreshSemaphore.Release();
            }
        }
    }

    private async Task RefreshActivePreviewFullResolutionAsync(CancellationToken cancellationToken)
    {
        if (_previewItem is null || _previewWindow is null)
        {
            return;
        }

        try
        {
            await _previewRefreshSemaphore.WaitAsync(cancellationToken);

            var activeItem = _previewItem;
            var previewWindow = _previewWindow;
            if (activeItem is null || previewWindow is null)
            {
                return;
            }

            var index = _loadedFrames.FindIndex(f => f.Item == activeItem);
            if (index < 0)
            {
                return;
            }

            _previewCacheCts?.Cancel();
            var loaded = _loadedFrames[index];
            var materializedFull = await MaterializeFrameAsync(loaded, cancellationToken);
            var fullImage = await _rustafits.RenderFullBitmapAsync(materializedFull, GetStfForFrame(materializedFull), cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !ReferenceEquals(_previewWindow, previewWindow) ||
                !ReferenceEquals(_previewItem, activeItem))
            {
                return;
            }

            _loadedFrames[index] = loaded with { FullImage = fullImage };
            PublishPreviewCacheState();
            previewWindow.RefreshImage(fullImage);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_previewRefreshSemaphore.CurrentCount == 0)
            {
                _previewRefreshSemaphore.Release();
            }
        }
    }

    private static (int Width, int Height) GetInteractivePreviewDimensions(LoadedFrameContext frame, bool scrubbing = false)
    {
        var longestSide = Math.Max(frame.Width, frame.Height);
        var maxLongSide = scrubbing ? PreviewInteractiveScrubbingMaxLongSide : PreviewInteractiveMaxLongSide;
        if (longestSide <= maxLongSide)
        {
            return (frame.Width, frame.Height);
        }

        var scale = maxLongSide / (double)longestSide;
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        return (width, height);
    }

    /// <summary>
    /// Returns a cached materialized (decoded + oriented) raw frame for the given
    /// preview item, decoding from disk only on a cache miss. STF slider scrubbing
    /// reuses the same pixel buffer to re-stretch in memory in realtime.
    /// </summary>
    private async Task<RustafitsService.LoadedFrame> GetOrCreateInteractiveRawFrameAsync(LoadedFrameContext context, CancellationToken cancellationToken)
    {
        if (_interactiveRawFrame is { } cached && ReferenceEquals(_interactiveRawItem, context.Item))
        {
            return cached;
        }

        var raw = await MaterializeFrameAsync(context, cancellationToken);
        _interactiveRawItem = context.Item;
        _interactiveRawFrame = raw;
        return raw;
    }

    private void InvalidateInteractiveRawFrame()
    {
        _interactiveRawItem = null;
        _interactiveRawFrame = null;
    }

    private async Task RebuildThumbnailsDeferredAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await RebuildThumbnailsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebuildThumbnailsAsync(CancellationToken cancellationToken)
    {
        if (_loadedFrames.Count == 0 || (IsBusy && !_isThumbnailRefreshRunning))
        {
            return;
        }

        await _thumbnailRefreshSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (_loadedFrames.Count == 0 || (IsBusy && !_isThumbnailRefreshRunning))
            {
                return;
            }

            await UpdateAutoRoiCenterAsync(cancellationToken);

            _previewCacheCts?.Cancel();

            _isThumbnailRefreshRunning = true;
            IsBusy = true;
            IsProgressVisible = true;
            ProgressValue = 0;
            ProgressMaximum = _loadedFrames.Count;

            // Snapshot fields used inside the parallel body so concurrent loop iterations
            // see a consistent set of inputs even if the caller mutates state mid-run.
            var framesSnapshot = _loadedFrames.ToArray();
            var roiSnapshot = _manualRoiRect;
            var totalFrames = framesSnapshot.Length;
            var completed = 0;

            var maxParallelism = Math.Max(2, Environment.ProcessorCount);
            using var gate = new SemaphoreSlim(maxParallelism);

            Status = $"Applying stretch (0/{totalFrames})";

            var pending = new Task[totalFrames];
            for (var i = 0; i < totalFrames; i++)
            {
                var loaded = framesSnapshot[i];
                pending[i] = Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var frameData = await MaterializeFrameAsync(loaded, cancellationToken);
                        var previews = await _rustafits.RenderPreviewBitmapsAsync(frameData, GetStfForFrame(frameData), roiSnapshot, loaded.Item.Metrics, cancellationToken);

                        loaded.Item.ThumbnailImage = previews.Full;
                        loaded.Item.RoiImage = previews.Roi;

                        var done = Interlocked.Increment(ref completed);
                        ProgressValue = done;
                        Status = $"Applying stretch ({done}/{totalFrames})";
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(pending);

            Status = "Stretch updated.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"Stretch update failed: {ex.Message}";
        }
        finally
        {
            _isThumbnailRefreshRunning = false;
            IsBusy = false;
            IsProgressVisible = false;
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
            _thumbnailRefreshSemaphore.Release();
        }
    }

    private async Task OpenPreviewAsync(FrameItem? item)
    {
        if (item is null)
        {
            return;
        }

        var currentIndex = _loadedFrames.FindIndex(f => f.Item == item);
        if (currentIndex < 0)
        {
            return;
        }

        if (_previewWindow is not null)
        {
            var currentPreviewVisibleFrameIndices = GetVisiblePreviewFrameIndices();
            var currentPreviewVisibleIndex = FindVisibleFrameIndex(currentPreviewVisibleFrameIndices, currentIndex);
            var cacheMiss = _loadedFrames[currentIndex].FullImage is null;
            if (cacheMiss)
            {
                var loadPosition = currentPreviewVisibleIndex >= 0 ? currentPreviewVisibleIndex + 1 : currentIndex + 1;
                var loadCount = currentPreviewVisibleFrameIndices.Count > 0 ? currentPreviewVisibleFrameIndices.Count : _loadedFrames.Count;
                var loadMessage = $"Loading frame {loadPosition}/{loadCount} from disk...";
                _previewVm?.SetPreviewStatus(loadMessage);
                Status = loadMessage;
            }

            if (!ReferenceEquals(_previewItem, item))
            {
                InvalidateInteractiveRawFrame();
            }
            _previewItem = item;
            SyncPreviewSelection(item);
            var existingImage = await GetOrCreateFullImageAsync(item);
            _previewVm?.SetItem(item);
            _previewVm?.UpdateFramePosition(Math.Max(0, currentPreviewVisibleIndex), currentPreviewVisibleFrameIndices.Count);
            PublishPreviewCacheState();
            _previewWindow.RefreshImage(existingImage);
            StartAdaptivePreviewCaching(item);
            if (cacheMiss)
            {
                var loadPosition = currentPreviewVisibleIndex >= 0 ? currentPreviewVisibleIndex + 1 : currentIndex + 1;
                var loadCount = currentPreviewVisibleFrameIndices.Count > 0 ? currentPreviewVisibleFrameIndices.Count : _loadedFrames.Count;
                _previewVm?.SetPreviewStatus($"Frame {loadPosition}/{loadCount} loaded from disk.");
                Status = $"Frame {loadPosition}/{loadCount} loaded from disk.";
            }
            _previewWindow.Activate();
            await Task.CompletedTask;
            return;
        }

        InvalidateInteractiveRawFrame();
        _previewItem = item;
        SyncPreviewSelection(item);
        var vm = new FramePreviewViewModel(
            item,
            () => StfShadows,
            value => StfShadows = value,
            () => StfMidtones,
            value => StfMidtones = value,
            () => StfHighlights,
            value => StfHighlights = value,
            () => StfTargetBackground,
            value => StfTargetBackground = value,
            () => _ = ApplyAutoStretchAsync(),
            BeginInteractiveStretch,
            EndInteractiveStretch,
            SetManualRoi,
            () => _manualRoiRect,
            NavigatePreviewAsync,
            NavigatePreviewToIndexAsync,
            TogglePreviewReject,
            () => ShowAccepted,
            value => ShowAccepted = value,
            () => ShowRejected,
            value => ShowRejected = value,
            FilterChips,
            GetVisiblePreviewFrameIndices,
            GetVisiblePreviewFrameData,
            RefreshPreviewVisibleFrames,
            () => _isAlignmentEnabled,
            SetAlignmentEnabled);
        var visibleFrameIndices = GetVisiblePreviewFrameIndices();
        var currentVisibleIndex = FindVisibleFrameIndex(visibleFrameIndices, currentIndex);
        vm.UpdateFramePosition(currentVisibleIndex, visibleFrameIndices.Count);
        PublishPreviewCacheState(vm);
        _previewVm = vm;
        _previewWindow = new PreviewWindow(vm);
        var current = await GetOrCreateFullImageAsync(item);
        PublishPreviewCacheState();
        _previewWindow.RefreshImage(current);
        StartAdaptivePreviewCaching(item);
        _previewWindow.Closed += (_, _) =>
        {
            _previewCacheCts?.Cancel();
            ClearAllFullImageCaches();
            SyncPreviewSelection(null);
            _previewWindow = null;
            _previewVm = null;
            _previewItem = null;
            InvalidateInteractiveRawFrame();

            if (_thumbnailRefreshPendingWhilePreviewOpen)
            {
                _thumbnailRefreshPendingWhilePreviewOpen = false;
                ScheduleThumbnailRebuild(immediate: true);
            }
        };

        _previewWindow.Show();
        await Task.CompletedTask;
    }

    private void SetManualRoi((double Left, double Top, double Width, double Height) rect)
    {
        _manualRoiRect = (
            Math.Clamp(rect.Left, 0.0, 1.0),
            Math.Clamp(rect.Top, 0.0, 1.0),
            Math.Clamp(rect.Width, 0.0, 1.0),
            Math.Clamp(rect.Height, 0.0, 1.0));
        _hasManualRoi = true;
        Status = "Manual ROI set.";

        // Always regenerate the ROI bitmaps immediately when a new ROI is drawn, even
        // when the preview window is open. ScheduleThumbnailRebuild defers in that case,
        // which would leave the per-frame ROI thumbnails stale until the preview closes.
        _stretchRefreshCts?.Cancel();
        _stretchRefreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _stretchRefreshCts = cts;
        _thumbnailRefreshPendingWhilePreviewOpen = false;
        _ = RebuildThumbnailsDeferredAsync(TimeSpan.Zero, cts.Token);
    }

    private async Task NavigatePreviewAsync(int direction)
    {
        if (_previewItem is null || direction == 0)
        {
            return;
        }

        var visibleFrameIndices = GetVisiblePreviewFrameIndices();
        if (visibleFrameIndices.Count == 0)
        {
            return;
        }

        var currentIndex = _loadedFrames.FindIndex(f => f.Item == _previewItem);
        if (currentIndex < 0)
        {
            return;
        }

        var currentVisibleIndex = FindVisibleFrameIndex(visibleFrameIndices, currentIndex);
        if (currentVisibleIndex < 0)
        {
            currentVisibleIndex = 0;
        }

        var nextVisibleIndex = Math.Clamp(currentVisibleIndex + direction, 0, visibleFrameIndices.Count - 1);
        if (nextVisibleIndex == currentVisibleIndex)
        {
            return;
        }

        var nextItem = _loadedFrames[visibleFrameIndices[nextVisibleIndex]].Item;
        await OpenPreviewAsync(nextItem);
    }

    private async Task NavigatePreviewToIndexAsync(int index)
    {
        var visibleFrameIndices = GetVisiblePreviewFrameIndices();
        if (visibleFrameIndices.Count == 0)
        {
            return;
        }

        var targetIndex = Math.Clamp(index, 0, _loadedFrames.Count - 1);
        if (!visibleFrameIndices.Contains(targetIndex))
        {
            return;
        }

        var targetItem = _loadedFrames[targetIndex].Item;
        if (targetItem == _previewItem)
        {
            return;
        }

        await OpenPreviewAsync(targetItem);
    }

    private void TogglePreviewReject()
    {
        ToggleFrameReject(_previewItem);
    }

    private void ToggleFrameReject(FrameItem? frame)
    {
        if (frame is null)
        {
            return;
        }

        var nextRejectedState = !frame.IsRejected;
        bool? nextManualOverride = nextRejectedState == frame.AutomaticRejected
            ? null
            : nextRejectedState;

        SetFrameRejected(frame, frame.AutomaticRejected, nextManualOverride);
        Status = frame.IsRejected
            ? $"Marked {frame.FileName} as rejected."
            : $"Marked {frame.FileName} as kept.";
    }

    private void SetFrameRejected(FrameItem frame, bool automaticRejected, bool? manualRejectedOverride = null, bool refreshStatistics = true)
    {
        var previousRejected = frame.IsRejected;
        var previousOverride = frame.ManualRejectedOverride;

        frame.SetAutomaticRejected(automaticRejected);
        frame.SetManualRejectedOverride(manualRejectedOverride);

        if (previousRejected == frame.IsRejected && previousOverride == frame.ManualRejectedOverride)
        {
            return;
        }

        if (!refreshStatistics)
        {
            return;
        }

        UpdateFrameStatistics();
        ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
        _previewVm?.NotifyFrameStateChanged();
    }

    private async Task<BitmapSource> GetOrCreateFullImageAsync(FrameItem item)
    {
        var index = _loadedFrames.FindIndex(f => f.Item == item);
        if (index < 0)
        {
            throw new InvalidOperationException("Frame is not loaded.");
        }

        var loaded = _loadedFrames[index];
        if (loaded.FullImage is not null)
        {
            return loaded.FullImage;
        }

        var materializedForFull = await MaterializeFrameAsync(loaded, CancellationToken.None);
        var fullImage = await _rustafits.RenderFullBitmapAsync(materializedForFull, GetStfForFrame(materializedForFull), CancellationToken.None);
        _loadedFrames[index] = loaded with { FullImage = fullImage };
        PublishPreviewCacheState();
        return fullImage;
    }

    private void StartPreviewCaching(FrameItem centerItem, int ahead, int behind)
    {
        _previewCacheCts?.Cancel();
        _previewCacheCts = new CancellationTokenSource();
        _ = PrecacheAroundPreviewAsync(centerItem, ahead, behind, _previewCacheCts.Token);
    }

    private void StartAdaptivePreviewCaching(FrameItem centerItem)
    {
        var (ahead, behind) = CalculateAdaptivePreviewCacheWindow(centerItem);
        StartPreviewCaching(centerItem, ahead, behind);
    }

    private async Task PrecacheAroundPreviewAsync(FrameItem centerItem, int ahead, int behind, CancellationToken cancellationToken)
    {
        var centerLoadedIndex = _loadedFrames.FindIndex(f => f.Item == centerItem);
        if (centerLoadedIndex < 0)
        {
            return;
        }

        // The vertical slider iterates the visible (filtered + sorted) order, not the
        // load order. Resolve neighbours in that same order so the pre-cache follows
        // whatever sort the user has applied. When the sort changes, FilteredFrames
        // (a ListCollectionView) reorders automatically, so the next caching pass will
        // naturally follow the new order.
        var visibleIndices = GetVisiblePreviewFrameIndices();
        var centerVisible = -1;
        for (var i = 0; i < visibleIndices.Count; i++)
        {
            if (visibleIndices[i] == centerLoadedIndex)
            {
                centerVisible = i;
                break;
            }
        }

        if (centerVisible < 0)
        {
            // Center item is not part of the current visible set (e.g., filtered out).
            // Nothing meaningful to pre-cache around it.
            return;
        }

        TrimFullImageCache(centerVisible, ahead, behind, visibleIndices);
        PublishPreviewCacheState();

        // Build a priority-ordered list of LOADED indices: nearest visible neighbours
        // first, ahead-weighted (+1, -1, +2, -2, +3, -3, ...). This keeps the forward
        // navigation bias while ensuring the immediate neighbour is warmed before
        // far-away ones, so even on cancellation at least one cached frame is retained.
        var priority = new List<int>(ahead + behind);
        var aheadStep = 1;
        var behindStep = 1;
        while (aheadStep <= ahead || behindStep <= behind)
        {
            if (aheadStep <= ahead)
            {
                var visIdx = centerVisible + aheadStep;
                if (visIdx < visibleIndices.Count)
                {
                    priority.Add(visibleIndices[visIdx]);
                }
                aheadStep++;
            }
            if (behindStep <= behind)
            {
                var visIdx = centerVisible - behindStep;
                if (visIdx >= 0)
                {
                    priority.Add(visibleIndices[visIdx]);
                }
                behindStep++;
            }
        }

        if (priority.Count == 0)
        {
            return;
        }

        try
        {
            // Always materialize the closest neighbour first and synchronously, so that
            // a rapid cancellation (e.g. user keeps navigating) still leaves at least
            // one cached frame around the new center.
            await EnsureFullImageCachedAsync(priority[0], cancellationToken);

            if (priority.Count == 1)
            {
                return;
            }

            // Warm the remaining frames with bounded parallelism. Two workers is enough
            // to overlap FITS read + decode + full-bitmap render across neighbours
            // without saturating the CPU or the file system.
            using var concurrency = new SemaphoreSlim(2);
            var tasks = new List<Task>(priority.Count - 1);
            for (var i = 1; i < priority.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idx = priority[i];
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await EnsureFullImageCachedAsync(idx, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        concurrency.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnsureFullImageCachedAsync(int index, CancellationToken cancellationToken)
    {
        LoadedFrameContext loaded;
        lock (_loadedFrames)
        {
            if ((uint)index >= (uint)_loadedFrames.Count)
            {
                return;
            }
            loaded = _loadedFrames[index];
            if (loaded.FullImage is not null)
            {
                return;
            }
        }

        var materializedCached = await MaterializeFrameAsync(loaded, cancellationToken);
        var full = await _rustafits.RenderFullBitmapAsync(materializedCached, GetStfForFrame(materializedCached), cancellationToken);

        lock (_loadedFrames)
        {
            if ((uint)index < (uint)_loadedFrames.Count && ReferenceEquals(_loadedFrames[index].Item, loaded.Item))
            {
                _loadedFrames[index] = _loadedFrames[index] with { FullImage = full };
            }
        }
        PublishPreviewCacheState();
    }

    private void ApplyThresholds()
    {
        foreach (var frame in Frames)
        {
            var key = NormalizeFilterValue(frame.FilterName);
            var t = GetThresholdsForKey(key);
            var trailEnabled = ShowTrailSlider && ShowTrailMetric;
            var fwhmEnabled = ShowFwhmSlider && ShowFwhmMetric;
            var fwhmArcsecEnabled = ShowFwhmArcsecSlider && ShowFwhmArcsecMetric;
            var sqmEnabled = ShowSqmSlider && ShowSqmMetric;
            var skyTempEnabled = ShowSkyTempSlider && ShowSkyTempMetric;
            var hfrEnabled = ShowHfrSlider && ShowHfrMetric;
            var eccentricityEnabled = ShowEccentricitySlider && ShowEccentricityMetric;
            var meanBackgroundEnabled = ShowMeanBackgroundSlider && ShowMeanBackgroundMetric;
            var starsEnabled = ShowStarsSlider && ShowStarsMetric;
            var scoreEnabled = ShowScoreSlider && ShowScoreMetric;

            if (fwhmEnabled && t.AutoCalcFwhmThreshold)
                t.MaxFwhm = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(frame).Max(f => f.Metrics.Fwhm);

            if (fwhmArcsecEnabled && t.AutoCalcFwhmArcsecThreshold)
            {
                var fwhmArcsecValues = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase) && f.Metrics.FwhmArcsec.HasValue)
                    .Select(f => f.Metrics.FwhmArcsec!.Value)
                    .ToList();
                t.MaxFwhmArcsec = fwhmArcsecValues.Count > 0 ? fwhmArcsecValues.Max() : 20.0;
            }

            if (sqmEnabled && t.AutoCalcSqmThreshold)
            {
                var sqmValues = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase) && f.Metrics.Sqm.HasValue)
                    .Select(f => f.Metrics.Sqm!.Value)
                    .ToList();
                t.MinSqm = sqmValues.Count > 0 ? sqmValues.Min() : 0.0;
            }

            if (skyTempEnabled && t.AutoCalcSkyTempThreshold)
            {
                var skyTempValues = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase) && f.Metrics.SkyTemp.HasValue)
                    .Select(f => f.Metrics.SkyTemp!.Value)
                    .ToList();
                t.MaxSkyTemp = skyTempValues.Count > 0 ? skyTempValues.Max() : 40.0;
            }

            if (hfrEnabled && t.AutoCalcHfrThreshold)
                t.MaxHfr = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(frame).Max(f => f.Metrics.Hfr);

            if (eccentricityEnabled && t.AutoCalcEccentricityThreshold)
                t.MaxEccentricity = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(frame).Max(f => f.Metrics.Eccentricity);

            if (meanBackgroundEnabled && t.AutoCalcMeanBackgroundThreshold)
                t.MaxMeanBackground = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(frame).Max(f => f.Metrics.MeanBackground);

            if (starsEnabled && t.AutoCalcStarsThreshold)
                t.MinStars = Frames.Where(f => string.Equals(NormalizeFilterValue(f.FilterName), key, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(frame).Min(f => (double)f.Metrics.StarCount);

            if (trailEnabled && t.AutoCalcTrailThreshold)
                t.MinSatelliteConfidence = 80;

            if (scoreEnabled && t.AutoCalcScoreThreshold)
                t.MinScore = 0.0;

            var effective = new Thresholds
            {
                MaxFwhm = fwhmEnabled ? t.MaxFwhm : double.PositiveInfinity,
                MaxFwhmArcsec = fwhmArcsecEnabled ? t.MaxFwhmArcsec : double.PositiveInfinity,
                MinSqm = sqmEnabled ? t.MinSqm : double.NegativeInfinity,
                MaxSkyTemp = skyTempEnabled ? t.MaxSkyTemp : double.PositiveInfinity,
                MaxHfr = hfrEnabled ? t.MaxHfr : double.PositiveInfinity,
                MaxEccentricity = eccentricityEnabled ? t.MaxEccentricity : double.PositiveInfinity,
                MaxMeanBackground = meanBackgroundEnabled ? t.MaxMeanBackground : double.PositiveInfinity,
                MinStars = starsEnabled ? t.MinStars : double.NegativeInfinity,
                MinSatelliteConfidence = (RejectSatelliteTrail && trailEnabled) ? t.MinSatelliteConfidence : 0,
                MinScore = scoreEnabled ? t.MinScore : 0,
            };

            var autoRejected = _rejection.ShouldReject(frame, effective);
            var reasons = autoRejected ? _rejection.GetRejectionReasons(frame, effective) : [];
            frame.SetRejectionReasons(reasons);
            SetFrameRejected(frame, autoRejected, frame.ManualRejectedOverride, refreshStatistics: false);
        }

        UpdateFrameStatistics();
        ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
    }

    internal void ExecuteMoveRejected(IReadOnlyCollection<string>? filterKeys = null)
    {
        if (string.IsNullOrWhiteSpace(RejectedFolder))
        {
            return;
        }

        try
        {
            var movedItems = _move.MoveRejected(Frames, RejectedFolder, filterKeys);

            if (movedItems.Count > 0)
            {
                // Remove moved frames from both observable and backing lists
                foreach (var item in movedItems)
                {
                    item.PropertyChanged -= FrameItem_PropertyChanged;
                    Frames.Remove(item);
                    var idx = _loadedFrames.FindIndex(f => ReferenceEquals(f.Item, item));
                    if (idx >= 0)
                        _loadedFrames.RemoveAt(idx);
                }

                // Clear selection if selected frame was moved
                if (SelectedFrame is not null && movedItems.Contains(SelectedFrame))
                    SelectedFrame = null;

                // Refresh statistics and the preview window
                FilteredFrames.Refresh();
                UpdateFrameStatistics();
                RefreshPreviewVisibleFrames();
                ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(MoveRejectedEnabled));
            }

            Status = $"Moved {movedItems.Count} rejected frame(s).";
        }
        catch (Exception ex)
        {
            Status = $"Move failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns a dictionary of filterKey → rejected-frame-count for all rejected frames.
    /// Only includes filters that have at least one rejected frame.
    /// </summary>
    internal IReadOnlyDictionary<string, int> GetRejectedCountByFilter()
    {
        return Frames
            .Where(f => f.IsRejected)
            .GroupBy(f => string.IsNullOrWhiteSpace(f.FilterName) ? "(no filter)" : f.FilterName)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task UpdateAutoRoiCenterAsync(CancellationToken cancellationToken)
    {
        if (_hasManualRoi || _loadedFrames.Count == 0)
        {
            return;
        }

        // Auto-detect a center point and build a normalized square ROI from it
        _manualRoiRect = _rustafits.DetectRoiNormalizedRect(await MaterializeFrameAsync(_loadedFrames[0], cancellationToken));
    }

    private static LoadedFrameContext CreateLoadedFrameContext(FrameItem item, RustafitsService.LoadedFrame frame, string filePath, bool rotate180, int shiftX = 0, int shiftY = 0)
    {
        return new LoadedFrameContext(
            item,
            filePath,
            frame.Width,
            frame.Height,
            frame.NormalizationMax,
            rotate180,
            shiftX,
            shiftY,
            frame.FocalLengthMm,
            frame.PixelSizeUm,
            frame.ExposureDateTime,
            frame.ExposureSeconds,
            frame.FilterName,
            frame.Sqm,
            frame.SkyTemp,
            null);
    }

    private async Task<RustafitsService.LoadedFrame> MaterializeFrameAsync(LoadedFrameContext context, CancellationToken cancellationToken)
    {
        var raw = await _rustafits.LoadRawFrameAsync(context.FilePath, cancellationToken);
        var oriented = _rustafits.ApplyOrientation(raw, context.Rotate180);
        return _isAlignmentEnabled ? _rustafits.ApplyShift(oriented, context.ShiftX, context.ShiftY) : oriented;
    }

    private (int Ahead, int Behind) CalculateAdaptivePreviewCacheWindow(FrameItem centerItem)
    {
        var centerIndex = _loadedFrames.FindIndex(f => f.Item == centerItem);
        if (centerIndex < 0 || _loadedFrames.Count == 0)
        {
            return (MinimumPreviewCacheAhead, MinimumPreviewCacheBehind);
        }

        var maxAheadAvailable = Math.Max(0, _loadedFrames.Count - 1 - centerIndex);
        var maxBehindAvailable = Math.Max(0, centerIndex);

        var availableCacheBytes = EstimateAvailablePreviewCacheBytes();
        var centerFrameBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex]);
        var remainingBytes = Math.Max(0L, availableCacheBytes - centerFrameBytes);

        var ahead = 0;
        var behind = 0;
        var preferAhead = true;

        while (remainingBytes > 0 && (ahead < maxAheadAvailable || behind < maxBehindAvailable))
        {
            var added = false;

            if ((preferAhead || behind >= MinimumPreviewCacheBehind) && ahead < maxAheadAvailable && ahead < MaximumPreviewCacheAhead)
            {
                var aheadBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex + ahead + 1]);
                if (aheadBytes <= remainingBytes)
                {
                    remainingBytes -= aheadBytes;
                    ahead++;
                    added = true;
                }
            }

            if ((!preferAhead || ahead >= MinimumPreviewCacheAhead) && behind < maxBehindAvailable && behind < MaximumPreviewCacheBehind)
            {
                var behindBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex - behind - 1]);
                if (behindBytes <= remainingBytes)
                {
                    remainingBytes -= behindBytes;
                    behind++;
                    added = true;
                }
            }

            if (!added)
            {
                if (preferAhead && behind < maxBehindAvailable && behind < MaximumPreviewCacheBehind)
                {
                    var behindBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex - behind - 1]);
                    if (behindBytes <= remainingBytes)
                    {
                        remainingBytes -= behindBytes;
                        behind++;
                        added = true;
                    }
                }
                else if (!preferAhead && ahead < maxAheadAvailable && ahead < MaximumPreviewCacheAhead)
                {
                    var aheadBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex + ahead + 1]);
                    if (aheadBytes <= remainingBytes)
                    {
                        remainingBytes -= aheadBytes;
                        ahead++;
                        added = true;
                    }
                }
            }

            if (!added)
            {
                break;
            }

            preferAhead = ahead < MinimumPreviewCacheAhead || (ahead < maxAheadAvailable && ahead < MaximumPreviewCacheAhead && (ahead - behind) < 6);
        }

        // Guarantee at least one cached neighbour whenever one is available, even under
        // tight memory pressure where the budget loop would otherwise return (0, 0).
        if (ahead == 0 && behind == 0)
        {
            if (maxAheadAvailable > 0)
            {
                ahead = 1;
            }
            else if (maxBehindAvailable > 0)
            {
                behind = 1;
            }
        }

        return (ahead, behind);
    }

    private static int CalculateFrameLoadParallelism(RustafitsService.LoadedFrame firstLoadedFrame, int totalBackgroundFrames)
    {
        if (totalBackgroundFrames <= 0)
        {
            return 1;
        }

        var cpuLimit = Math.Max(1, Environment.ProcessorCount);
        var frameLoadBytes = EstimateFrameLoadWorkingSetBytes(firstLoadedFrame);

        var memoryStatus = MEMORYSTATUSEX.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.ullAvailPhys <= 0)
        {
            return Math.Min(cpuLimit, totalBackgroundFrames);
        }

        var freeBytes = Math.Max(0L, (long)memoryStatus.ullAvailPhys - FrameLoadReservedBytes);
        var memoryLimitedParallelism = (int)Math.Max(1L, freeBytes / frameLoadBytes);

        return Math.Max(1, Math.Min(totalBackgroundFrames, Math.Min(cpuLimit, memoryLimitedParallelism)));
    }

    private static long EstimateFrameLoadWorkingSetBytes(RustafitsService.LoadedFrame frame)
    {
        var monoBytes = frame.Pixels is null ? 0L : (long)frame.Pixels.Length * sizeof(float);
        var colorBytes = 0L;
        if (frame.ColorChannels is { Length: > 0 } channels)
        {
            for (var i = 0; i < channels.Length; i++)
            {
                if (channels[i] is { Length: > 0 } channel)
                {
                    colorBytes += (long)channel.Length * sizeof(float);
                }
            }
        }

        var trueFrameBytes = Math.Max((long)frame.Width * frame.Height * sizeof(float), monoBytes + colorBytes);

        // Frame loading and preview generation create additional temporary buffers.
        return Math.Max(64L * 1024 * 1024, trueFrameBytes * 4L);
    }

    private static long EstimatePreviewFrameBytes(LoadedFrameContext frame)
    {
        var pixelBytes = (long)frame.Width * frame.Height * 3L;
        return Math.Max(8L * 1024 * 1024, pixelBytes + (pixelBytes / 10));
    }

    private static long EstimateAvailablePreviewCacheBytes()
    {
        var memoryStatus = MEMORYSTATUSEX.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.ullAvailPhys <= 0)
        {
            return 128L * 1024 * 1024;
        }

        var freeBytes = Math.Max(0L, (long)memoryStatus.ullAvailPhys - PreviewCacheReservedBytes);
        return Math.Max(64L * 1024 * 1024, freeBytes);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Create()
        {
            return new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
        }
    }

    private void TrimFullImageCache(int centerVisibleIndex, int ahead, int behind, IReadOnlyList<int> visibleIndices)
    {
        // Build the set of loaded-frame indices that should be RETAINED, expressed in
        // visible (sorted/filtered) order so the cache follows the slider rather than
        // the on-disk load order.
        var retain = new HashSet<int>();
        var minVisible = Math.Max(0, centerVisibleIndex - behind);
        var maxVisible = Math.Min(visibleIndices.Count - 1, centerVisibleIndex + ahead);
        for (var v = minVisible; v <= maxVisible; v++)
        {
            retain.Add(visibleIndices[v]);
        }

        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            if (retain.Contains(i))
            {
                continue;
            }

            if (_loadedFrames[i].FullImage is null)
            {
                continue;
            }

            _loadedFrames[i] = _loadedFrames[i] with { FullImage = null };
        }
    }

    private void ClearAllFullImageCaches()
    {
        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            if (_loadedFrames[i].FullImage is not null)
            {
                _loadedFrames[i] = _loadedFrames[i] with { FullImage = null };
            }
        }

        PublishPreviewCacheState();
    }

    private void PublishPreviewCacheState(FramePreviewViewModel? targetVm = null)
    {
        var vm = targetVm ?? _previewVm;
        if (vm is null)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => PublishPreviewCacheState(vm)));
            return;
        }

        var visibleFrameIndices = GetVisiblePreviewFrameIndices();
        var cachedIndices = new List<int>();
        for (var i = 0; i < visibleFrameIndices.Count; i++)
        {
            var loadedFrameIndex = visibleFrameIndices[i];
            if (_loadedFrames[loadedFrameIndex].FullImage is not null)
            {
                cachedIndices.Add(i);
            }
        }

        vm.UpdateCachedFrameIndices(cachedIndices);
    }

    private IReadOnlyList<int> GetVisiblePreviewFrameIndices()
    {
        if (_loadedFrames.Count == 0)
        {
            return [];
        }

        var loadedIndexByItem = new Dictionary<FrameItem, int>(_loadedFrames.Count);
        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            loadedIndexByItem[_loadedFrames[i].Item] = i;
        }

        var visibleIndices = new List<int>(_loadedFrames.Count);
        foreach (var candidate in FilteredFrames)
        {
            if (candidate is not FrameItem frame)
            {
                continue;
            }

            if (loadedIndexByItem.TryGetValue(frame, out var loadedIndex))
            {
                visibleIndices.Add(loadedIndex);
            }
        }

        return visibleIndices;
    }

    private IReadOnlyList<(double Score, bool IsRejected)> GetVisiblePreviewFrameData()
    {
        var indices = GetVisiblePreviewFrameIndices();
        var result = new (double Score, bool IsRejected)[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            var item = _loadedFrames[indices[i]].Item;
            result[i] = (item.OverallScore, item.IsRejected);
        }
        return result;
    }

    private static int FindVisibleFrameIndex(IReadOnlyList<int> visibleFrameIndices, int loadedFrameIndex)
    {
        for (var i = 0; i < visibleFrameIndices.Count; i++)
        {
            if (visibleFrameIndices[i] == loadedFrameIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private void RefreshPreviewVisibleFrames()
    {
        if (_previewVm is null)
        {
            return;
        }

        var visibleFrameIndices = GetVisiblePreviewFrameIndices();
        if (visibleFrameIndices.Count == 0)
        {
            _previewVm.UpdateFramePosition(0, 0);
            PublishPreviewCacheState();
            return;
        }

        var currentLoadedIndex = _previewItem is null
            ? -1
            : _loadedFrames.FindIndex(f => ReferenceEquals(f.Item, _previewItem));
        var currentVisibleIndex = currentLoadedIndex >= 0
            ? FindVisibleFrameIndex(visibleFrameIndices, currentLoadedIndex)
            : -1;

        if (currentVisibleIndex < 0)
        {
            // The current preview frame may have just become invisible (for example,
            // toggled to rejected while "accepted" visibility is active). In that case,
            // keep the user's relative position instead of jumping back to the first item.
            var fallbackVisibleIndex = Math.Clamp(_previewVm.CurrentFrameIndex, 0, visibleFrameIndices.Count - 1);
            var fallbackLoadedIndex = visibleFrameIndices[fallbackVisibleIndex];
            var fallbackItem = _loadedFrames[fallbackLoadedIndex].Item;
            _ = OpenPreviewAsync(fallbackItem);
            return;
        }

        _previewVm.UpdateFramePosition(currentVisibleIndex, visibleFrameIndices.Count);
        PublishPreviewCacheState();

        // Visible order may have changed (sort/filter toggled), so the previous
        // pre-cache window no longer reflects the user's navigation neighbours.
        // Re-launch caching around the same center item — TrimFullImageCache will
        // evict any cached frames that fell out of the new window, and the priority
        // walk will warm the new immediate neighbours first.
        if (_previewItem is FrameItem currentCenter)
        {
            StartAdaptivePreviewCaching(currentCenter);
        }
    }

    private void ResetFrameStatistics()
    {
        OnPropertyChanged(nameof(TotalFrameCount));
        RejectedFrameCount = 0;
        ApprovedFrameCount = 0;
        OverallAcceptedRatio = 0;
        TotalIntegrationTimeText = string.Empty;
        AcceptedIntegrationTimeText = string.Empty;
        FilterSummaries.Clear();
        FwhmRejectedFrameCount = 0;
        HfrRejectedFrameCount = 0;
        SqmRejectedFrameCount = 0;
        SkyTempRejectedFrameCount = 0;
        EccentricityRejectedFrameCount = 0;
        MeanBackgroundRejectedFrameCount = 0;
        StarCountRejectedFrameCount = 0;
        SatelliteTrailRejectedFrameCount = 0;
    }

    private static string FormatIntegrationHours(double seconds)
    {
        var hours = seconds / 3600.0;
        return hours >= 1.0 ? $"{hours:F1} h" : $"{seconds / 60.0:F0} min";
    }

    private void UpdateFrameStatistics()
    {
        var visibleFrames = GetVisibleFramesForStatistics().ToList();
        OnPropertyChanged(nameof(TotalFrameCount));
        RejectedFrameCount = visibleFrames.Count(frame => frame.IsRejected);
        ApprovedFrameCount = Math.Max(0, TotalFrameCount - RejectedFrameCount);

        // overall ratio and integration time
        OverallAcceptedRatio = TotalFrameCount > 0 ? (double)ApprovedFrameCount / TotalFrameCount : 0;
        var totalSec = visibleFrames.Sum(f => f.ExposureSeconds ?? 0);
        var acceptedSec = visibleFrames.Where(f => !f.IsRejected).Sum(f => f.ExposureSeconds ?? 0);
        TotalIntegrationTimeText = totalSec > 0 ? FormatIntegrationHours(totalSec) : string.Empty;
        AcceptedIntegrationTimeText = acceptedSec > 0 ? FormatIntegrationHours(acceptedSec) : string.Empty;

        // per-filter summaries
        var filterGroups = visibleFrames
            .GroupBy(f => string.IsNullOrWhiteSpace(f.FilterName) ? "(none)" : f.FilterName.Trim())
            .OrderBy(g => g.Key)
            .ToList();

        var existingByKey = FilterSummaries.ToDictionary(s => s.FilterName, StringComparer.OrdinalIgnoreCase);
        var newKeys = filterGroups.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // remove obsolete
        for (var i = FilterSummaries.Count - 1; i >= 0; i--)
        {
            if (!newKeys.Contains(FilterSummaries[i].FilterName))
                FilterSummaries.RemoveAt(i);
        }

        foreach (var group in filterGroups)
        {
            var total = group.Count();
            var accepted = group.Count(f => !f.IsRejected);
            var rejected = total - accepted;
            var ratio = total > 0 ? (double)accepted / total : 0;
            var filterTotalSec = group.Sum(f => f.ExposureSeconds ?? 0);
            var filterAccSec = group.Where(f => !f.IsRejected).Sum(f => f.ExposureSeconds ?? 0);
            var integText = filterAccSec > 0 ? FormatIntegrationHours(filterAccSec) : (filterTotalSec > 0 ? FormatIntegrationHours(filterTotalSec) : string.Empty);

            if (existingByKey.TryGetValue(group.Key, out var vm))
            {
                vm.Total = total;
                vm.Accepted = accepted;
                vm.Rejected = rejected;
                vm.AcceptedRatio = ratio;
                vm.RatioText = $"{ratio:P0}";
                vm.IntegrationTimeText = integText;
            }
            else
            {
                FilterSummaries.Add(new FilterSummaryViewModel
                {
                    FilterName = group.Key,
                    Total = total,
                    Accepted = accepted,
                    Rejected = rejected,
                    AcceptedRatio = ratio,
                    RatioText = $"{ratio:P0}",
                    IntegrationTimeText = integText,
                });
            }
        }
        FwhmRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.Fwhm > MaxFwhm);
        FwhmArcsecRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.FwhmArcsec.HasValue && frame.Metrics.FwhmArcsec.Value > MaxFwhmArcsec);
        SqmRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.Sqm.HasValue && frame.Metrics.Sqm.Value < MinSqm);
        SkyTempRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.SkyTemp.HasValue && frame.Metrics.SkyTemp.Value > MaxSkyTemp);
        HfrRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.Hfr > MaxHfr);
        EccentricityRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.Eccentricity > MaxEccentricity);
        MeanBackgroundRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.MeanBackground > MaxMeanBackground);
        StarCountRejectedFrameCount = visibleFrames.Count(frame => frame.Metrics.StarCount < MinStars);
        ScoreRejectedFrameCount = visibleFrames.Count(frame => MinScore > 0 && frame.OverallScore < MinScore);
        SatelliteTrailRejectedFrameCount = RejectSatelliteTrail
            ? visibleFrames.Count(frame => frame.Metrics.SatelliteTrailConfidence >= MinSatelliteConfidence)
            : 0;
    }

    private void SyncPreviewSelection(FrameItem? activeItem)
    {
        SelectedFrame = activeItem;

        foreach (var frame in Frames)
        {
            frame.IsPreviewActive = ReferenceEquals(frame, activeItem);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Coalesces high-frequency progress updates from many background workers into a single
    /// UI-thread tick every ~150 ms. Workers post updates via lock-free counters; the timer
    /// composes a single status string and pushes <see cref="ProgressValue"/> so the bar
    /// advances steadily even when the listview is busy rendering.
    /// </summary>
    private sealed class BulkLoadProgressReporter : IDisposable
    {
        private readonly Action<string> _publishStatus;
        private readonly Action<double> _publishProgress;
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly int _total;
        private int _completed;
        private int _active;
        private int _skipped;
        private string? _currentFile;
        private string? _lastPublishedStatus;
        private int _lastPublishedCompleted = -1;

        public BulkLoadProgressReporter(int total, Action<string> publishStatus, Action<double> publishProgress)
        {
            _total = total;
            _publishStatus = publishStatus;
            _publishProgress = publishProgress;
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _timer.Tick += (_, _) => Flush();
        }

        public void Start() => _timer.Start();

        public void NotifyStarted(string fileName)
        {
            Interlocked.Increment(ref _active);
            Volatile.Write(ref _currentFile, fileName);
        }

        public void NotifyCompleted(string? fileName = null)
        {
            Interlocked.Increment(ref _completed);
            Interlocked.Decrement(ref _active);
            if (fileName is not null)
            {
                Volatile.Write(ref _currentFile, fileName);
            }
        }

        public void NotifySkipped(string? fileName = null)
        {
            Interlocked.Increment(ref _completed);
            Interlocked.Increment(ref _skipped);
            Interlocked.Decrement(ref _active);
            if (fileName is not null)
            {
                Volatile.Write(ref _currentFile, fileName);
            }
        }

        public void NotifyFirstFrameStarted(string fileName)
        {
            Volatile.Write(ref _currentFile, fileName);
        }

        public void NotifyFirstFrameCompleted(string fileName)
        {
            Interlocked.Increment(ref _completed);
            Volatile.Write(ref _currentFile, fileName);
        }

        public void NotifyFirstFrameSkipped(string fileName)
        {
            Interlocked.Increment(ref _completed);
            Interlocked.Increment(ref _skipped);
            Volatile.Write(ref _currentFile, fileName);
        }

        private void Flush()
        {
            var completed = Volatile.Read(ref _completed);
            var active = Volatile.Read(ref _active);
            var skipped = Volatile.Read(ref _skipped);
            var current = Volatile.Read(ref _currentFile);

            if (completed != _lastPublishedCompleted)
            {
                _publishProgress(completed);
                _lastPublishedCompleted = completed;
            }

            var status = current is null
                ? $"Loading {completed}/{_total} \u2022 active: {active}"
                : $"Loading {completed}/{_total} \u2022 active: {active} \u2022 current: {current}";
            if (skipped > 0)
            {
                status += $" \u2022 skipped: {skipped}";
            }

            if (!string.Equals(status, _lastPublishedStatus, StringComparison.Ordinal))
            {
                _publishStatus(status);
                _lastPublishedStatus = status;
            }
        }

        public void Stop()
        {
            _timer.Stop();
            Flush();
        }

        public void Dispose() => Stop();
    }
}
