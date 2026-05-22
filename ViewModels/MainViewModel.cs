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
    private const int PreviewFullResolutionIdleMs = 220;
    private const int MinimumPreviewCacheAhead = 8;
    private const int MinimumPreviewCacheBehind = 2;
    private const int MaximumPreviewCacheAhead = 32;
    private const int MaximumPreviewCacheBehind = 12;
    private const long PreviewCacheReservedBytes = 1024L * 1024 * 1024;
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

    private string? _inputFolder;
    private string? _rejectedFolder;
    private bool _includeSubfolders;
    private string _status = "Ready";
    private bool _isBusy;
    private double _progressValue;
    private int _progressMaximum = 1;
    private bool _isProgressVisible;
    private double _maxFwhm = 8.0;
    private double _maxHfr = 4.5;
    private double _maxEccentricity = 0.6;
    private double _maxMeanBackground = 2000.0;
    private double _minStars;
    private double _minScore;
    private bool _rejectSatelliteTrail = true;
    private int _minSatelliteConfidence = 80;
    private double _stfShadows;
    private double _stfMidtones = 0.5;
    private double _stfHighlights = 1.0;
    private double _stfTargetBackground = 0.25;
    private double? _sessionFocalLengthMm;
    private double? _sessionPixelSizeUm;
    private int _approvedFrameCount;
    private int _eccentricityRejectedFrameCount;
    private int _fwhmRejectedFrameCount;
    private int _hfrRejectedFrameCount;
    private int _meanBackgroundRejectedFrameCount;
    private int _rejectedFrameCount;
    private int _satelliteTrailRejectedFrameCount;
    private int _sqmRejectedFrameCount;
    private int _skyTempRejectedFrameCount;
    private int _starCountRejectedFrameCount;
    private int _scoreRejectedFrameCount;
    private bool _hasManualRoi;
    private bool _autoStretchPerFrame = true;
    private bool _skipRejectedInPreview;
    private bool _showAccepted = true;
    private bool _showRejected = true;
    private bool _isUpdatingFilterSelection;
    private FrameItem? _selectedFrame;
    private double _minSqm;
    private double _maxSkyTemp = 40.0;

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
    private string _totalIntegrationTimeText = string.Empty;
    private string _acceptedIntegrationTimeText = string.Empty;
    private double _overallAcceptedRatio;

    public RangeObservableCollection<FrameItem> Frames { get; } = [];
    public ICollectionView FilteredFrames { get; }
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];
    public ObservableCollection<FilterSummaryViewModel> FilterSummaries { get; } = [];
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
            SaveFolderSettings();
            ((RelayCommand)LoadFramesCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value) return;
            _includeSubfolders = value;
            OnPropertyChanged();
            SaveFolderSettings();
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
            OnStretchSettingsChanged();
        }
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

    public double MaxFwhm
    {
        get => _maxFwhm;
        set
        {
            if (Math.Abs(_maxFwhm - value) < double.Epsilon) return;
            _maxFwhm = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MaxSkyTemp
    {
        get => _maxSkyTemp;
        set
        {
            if (Math.Abs(_maxSkyTemp - value) < double.Epsilon) return;
            _maxSkyTemp = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MaxHfr
    {
        get => _maxHfr;
        set
        {
            if (Math.Abs(_maxHfr - value) < double.Epsilon) return;
            _maxHfr = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MinSqm
    {
        get => _minSqm;
        set
        {
            if (Math.Abs(_minSqm - value) < double.Epsilon) return;
            _minSqm = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MaxEccentricity
    {
        get => _maxEccentricity;
        set
        {
            if (Math.Abs(_maxEccentricity - value) < double.Epsilon) return;
            _maxEccentricity = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MaxMeanBackground
    {
        get => _maxMeanBackground;
        set
        {
            if (Math.Abs(_maxMeanBackground - value) < double.Epsilon) return;
            _maxMeanBackground = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MinStars
    {
        get => _minStars;
        set
        {
            if (Math.Abs(_minStars - value) < double.Epsilon) return;
            _minStars = value;
            OnPropertyChanged();
            ApplyThresholds();
        }
    }

    public double MinScore
    {
        get => _minScore;
        set
        {
            if (Math.Abs(_minScore - value) < double.Epsilon) return;
            _minScore = value;
            OnPropertyChanged();
            ApplyThresholds();
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
        get => _minSatelliteConfidence;
        set
        {
            if (_minSatelliteConfidence == value) return;
            _minSatelliteConfidence = value;
            OnPropertyChanged();
            ApplyThresholds();
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

        AddSortRule(initialField: DefaultPrimarySortField, initialDirection: SortDirectionOptions[0]);

        var settings = _settings.Load();
        InputFolder = settings.InputFolder;
        RejectedFolder = settings.RejectedFolder;
        _includeSubfolders = settings.IncludeSubfolders;

        // Fire-and-forget update check — non-blocking, never throws to the caller.
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        var latest = await _updateCheck.GetLatestVersionAsync();
        if (!string.IsNullOrWhiteSpace(latest))
            UpdateBanner.ShowUpdate(latest);
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
                .Select(frame => NormalizeFilterKey(frame.FilterName, out var displayName) ? (Key: NormalizeFilterValue(frame.FilterName), DisplayName: displayName) : default)
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .DistinctBy(x => x.Key)
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var filter in filters)
            {
                var chip = new FilterChipViewModel(filter.Key!, filter.DisplayName!, isSelected: true);
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
        var root = InputFolder?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(root) || !dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || dir.Length <= root.Length)
        {
            return null;
        }

        return dir[(root.Length + 1)..];
    }

    private void BrowseInput()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Select folder with FITS/XISF frames";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            InputFolder = dialog.SelectedPath;
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
        _settings.Save(new AppSettings
        {
            InputFolder = InputFolder,
            RejectedFolder = RejectedFolder,
            IncludeSubfolders = IncludeSubfolders
        });
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
            IProgress<string> statusProgress = new Progress<string>(message => Status = message);

            if (files.Count == 0)
            {
                Status = "No FITS/XISF frames found.";
                return;
            }

            var firstSuccessfulIndex = -1;
            RustafitsService.LoadedFrame? orientationReference = null;
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                Status = $"Loading first frame {i + 1}/{files.Count}: {Path.GetFileName(file)}";

                try
                {
                    var raw = await _rustafits.LoadRawFrameAsync(file, CancellationToken.None);
                    var metrics = _rustafits.AnalyzeFrame(raw);
                    var autoStf = _rustafits.ComputeAutoStretch(raw, _stfTargetBackground);
                    _stfShadows = autoStf.Shadows;
                    _stfMidtones = autoStf.Midtones;
                    _stfHighlights = autoStf.Highlights;
                    OnPropertyChanged(nameof(StfShadows));
                    OnPropertyChanged(nameof(StfMidtones));
                    OnPropertyChanged(nameof(StfHighlights));
                    var roiCenter = _rustafits.DetectRoiNormalizedCenter(raw);
                    var longestSide = Math.Max(raw.Width, raw.Height);
                    var roiSize = longestSide > 0 ? 160.0 / longestSide : 0.25;
                    _manualRoiRect = (Math.Clamp(roiCenter.X - roiSize / 2, 0, 1 - roiSize), Math.Clamp(roiCenter.Y - roiSize / 2, 0, 1 - roiSize), roiSize, roiSize);
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
                        Metrics = metrics
                    };

                    item.PropertyChanged += FrameItem_PropertyChanged;
                    Frames.Add(item);
                    _loadedFrames.Add(CreateLoadedFrameContext(item, raw, filePath: file, rotate180: false));
                    orientationReference = raw;
                    SessionFocalLengthMm ??= raw.FocalLengthMm;
                    SessionPixelSizeUm ??= raw.PixelSizeUm;
                    loadedCount++;
                    firstSuccessfulIndex = i;
                    ProgressValue = i + 1;
                    Status = $"Loaded {loadedCount}/{files.Count}. Building previews and metrics in background...";
                    break;
                }
                catch (Exception ex)
                {
                    skippedCount++;
                    Status = $"Skipped {Path.GetFileName(file)} ({skippedCount} skipped): {ex.Message}";
                    ProgressValue = i + 1;
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
                var startedBackgroundFrames = 0;
                var activeBackgroundFrames = 0;
                var completedBackgroundFrames = 0;
                var maxParallelism = Math.Max(2, Environment.ProcessorCount);
                using var gate = new SemaphoreSlim(maxParallelism);

                statusProgress.Report($"Loaded {loadedCount}/{files.Count}. Queueing {totalBackgroundFrames} remaining frame(s) for decode, orientation, metrics, and preview generation...");

                var pending = filesToProcess.Select(async entry =>
                {
                    await gate.WaitAsync(CancellationToken.None);
                    var fileName = Path.GetFileName(entry.File);
                    var activeCount = Interlocked.Increment(ref activeBackgroundFrames);
                    var startedCount = Interlocked.Increment(ref startedBackgroundFrames);
                    statusProgress.Report($"Background processing {startedCount}/{totalBackgroundFrames}: decoding {fileName} (active: {activeCount}, completed: {Volatile.Read(ref completedBackgroundFrames)})");

                    try
                    {
                        var raw = await _rustafits.LoadRawFrameAsync(entry.File, CancellationToken.None);
                        statusProgress.Report($"Background processing {startedCount}/{totalBackgroundFrames}: orienting {fileName} (active: {Volatile.Read(ref activeBackgroundFrames)}, completed: {Volatile.Read(ref completedBackgroundFrames)})");
                        var rotate180 = _rustafits.ShouldRotate180ForOrientation(raw, orientationReference);
                        var oriented = _rustafits.ApplyOrientation(raw, rotate180);

                        statusProgress.Report($"Background processing {startedCount}/{totalBackgroundFrames}: computing metrics for {fileName} (active: {Volatile.Read(ref activeBackgroundFrames)}, completed: {Volatile.Read(ref completedBackgroundFrames)})");
                        var metrics = _rustafits.AnalyzeFrame(oriented);

                        statusProgress.Report($"Background processing {startedCount}/{totalBackgroundFrames}: building previews for {fileName} (active: {Volatile.Read(ref activeBackgroundFrames)}, completed: {Volatile.Read(ref completedBackgroundFrames)})");
                        var previews = await _rustafits.RenderPreviewBitmapsAsync(oriented, GetStfForFrame(oriented), _manualRoiRect, metrics, CancellationToken.None);

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
                            Metrics = metrics
                        };

                        item.PropertyChanged += FrameItem_PropertyChanged;
                        return (Item: item, Frame: oriented, Rotate180: rotate180, Error: (Exception?)null, SourceIndex: entry.SourceIndex, FileName: item.FileName);
                    }
                    catch (Exception ex)
                    {
                        return (Item: (FrameItem?)null, Frame: (RustafitsService.LoadedFrame?)null, Rotate180: false, Error: ex, SourceIndex: entry.SourceIndex, FileName: Path.GetFileName(entry.File));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeBackgroundFrames);
                        gate.Release();
                    }
                }).ToList();

                while (pending.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pending);
                    pending.Remove(completedTask);
                    var result = await completedTask;
                    var finishedBackgroundFrames = Interlocked.Increment(ref completedBackgroundFrames);

                    if (result.Item is not null && result.Frame is not null)
                    {
                        Frames.Add(result.Item);
                        _loadedFrames.Add(CreateLoadedFrameContext(result.Item, result.Frame, result.Item.FilePath, result.Rotate180));
                        SessionFocalLengthMm ??= result.Frame.FocalLengthMm;
                        SessionPixelSizeUm ??= result.Frame.PixelSizeUm;
                        loadedCount++;
                        Status = $"Loaded {loadedCount}/{files.Count}. Background processing complete for {finishedBackgroundFrames}/{totalBackgroundFrames}: {result.Item.FileName} (active: {Volatile.Read(ref activeBackgroundFrames)})";
                    }
                    else if (result.Error is not null)
                    {
                        skippedCount++;
                        Status = $"Skipped {result.FileName} ({skippedCount} skipped). Background processing complete for {finishedBackgroundFrames}/{totalBackgroundFrames} (active: {Volatile.Read(ref activeBackgroundFrames)}): {result.Error.Message}";
                    }

                    ProgressValue += 1;
                }
            }

            Status = "Finalizing frame comparisons...";
            UpdateFrameComparisons();
            Status = "Building filter chips...";
            RebuildFilterChips();
            Status = "Initializing rejection thresholds...";
            InitializeThresholdsFromLoadedFrames();
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
            _maxFwhm = session.MaxFwhm;
            OnPropertyChanged(nameof(MaxFwhm));
            _maxHfr = session.MaxHfr;
            OnPropertyChanged(nameof(MaxHfr));
            _maxEccentricity = session.MaxEccentricity;
            OnPropertyChanged(nameof(MaxEccentricity));
            _maxMeanBackground = session.MaxMeanBackground;
            OnPropertyChanged(nameof(MaxMeanBackground));
            _minStars = session.MinStars;
            OnPropertyChanged(nameof(MinStars));
            _minScore = session.MinScore;
            OnPropertyChanged(nameof(MinScore));
            _minSqm = session.MinSqm;
            OnPropertyChanged(nameof(MinSqm));
            _maxSkyTemp = session.MaxSkyTemp;
            OnPropertyChanged(nameof(MaxSkyTemp));
            _minSatelliteConfidence = session.MinSatelliteConfidence;
            OnPropertyChanged(nameof(MinSatelliteConfidence));
            _rejectSatelliteTrail = session.RejectSatelliteTrail;
            OnPropertyChanged(nameof(RejectSatelliteTrail));
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
                            var rotate180 = orientationReference is not null && _rustafits.ShouldRotate180ForOrientation(raw, orientationReference);
                            var oriented = _rustafits.ApplyOrientation(raw, rotate180);
                            var metrics = _rustafits.AnalyzeFrame(oriented);
                            var previews = await _rustafits.RenderPreviewBitmapsAsync(oriented, GetStfForFrame(oriented), _manualRoiRect, metrics, CancellationToken.None);

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
                                Metrics = metrics
                            };

                            newItem.PropertyChanged += FrameItem_PropertyChanged;
                            Frames.Add(newItem);
                            _loadedFrames.Add(CreateLoadedFrameContext(newItem, oriented, file, rotate180));
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

        var avgFwhm = Frames.Average(f => f.Metrics.Fwhm);
        var avgHfr = Frames.Average(f => f.Metrics.Hfr);
        var avgStars = Frames.Average(f => (double)f.Metrics.StarCount);
        var avgEcc = Frames.Average(f => f.Metrics.Eccentricity);
        var avgBg = Frames.Average(f => f.Metrics.MeanBackground);

        foreach (var frame in Frames)
        {
            frame.FwhmIndicatorBrush = CompareLowerIsBetter(frame.Metrics.Fwhm, avgFwhm, green, yellow, red);
            frame.HfrIndicatorBrush = CompareLowerIsBetter(frame.Metrics.Hfr, avgHfr, green, yellow, red);
            frame.StarsIndicatorBrush = CompareHigherIsBetter(frame.Metrics.StarCount, avgStars, green, yellow, red);
            frame.EccentricityIndicatorBrush = CompareLowerIsBetter(frame.Metrics.Eccentricity, avgEcc, green, yellow, red);
            frame.MeanBackgroundIndicatorBrush = CompareLowerIsBetter(frame.Metrics.MeanBackground, avgBg, green, yellow, red);
            frame.TrailIndicatorBrush = frame.Metrics.SatelliteTrailConfidence >= 60 ? red : green;

            // Score is computed below using rank-percentile logic (see ComputePercentileScores)
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
    /// Weights reflect typical astro importance:
    ///   FWHM (3.0)  — sharpness / seeing, most critical
    ///   Eccentricity (2.5) — star roundness (tracking, tilt)
    ///   HFR (1.5)   — correlated with FWHM, secondary confirmation
    ///   Stars (1.5) — cloud coverage / transparency
    ///   Mean BG (0.5) — light pollution / gradient, less decisive alone
    ///   Trail (2.0) — satellite/aircraft contamination, binary-ish
    /// </summary>
    private void ComputePercentileScores()
    {
        var frames = Frames;
        if (frames.Count == 0) return;

        const double fwhmWeight  = 3.0;
        const double eccWeight   = 2.5;
        const double hfrWeight   = 1.5;
        const double starsWeight = 1.5;
        const double bgWeight    = 0.5;
        const double trailWeight = 2.0;
        const double totalWeight = fwhmWeight + eccWeight + hfrWeight + starsWeight + bgWeight + trailWeight;

        // Converts a raw value array to [0,1] rank-percentile per frame.
        // Ties receive the average rank of their group.
        // 1.0 = best frame, 0.0 = worst frame.
        static double[] RankPercentile(double[] values, bool lowerIsBetter)
        {
            var n = values.Length;
            if (n == 1) return [1.0];

            var indexed = values.Select((v, i) => (v, i)).ToArray();
            var sorted = lowerIsBetter
                ? indexed.OrderBy(x => x.v).ToArray()
                : indexed.OrderByDescending(x => x.v).ToArray();

            var percentiles = new double[n];
            var rank = 0;
            while (rank < n)
            {
                var val = sorted[rank].v;
                var tieEnd = rank;
                while (tieEnd + 1 < n && sorted[tieEnd + 1].v == val) tieEnd++;
                var avgRank = (rank + tieEnd) / 2.0;
                var pct = 1.0 - avgRank / (n - 1.0);
                for (var t = rank; t <= tieEnd; t++)
                    percentiles[sorted[t].i] = pct;
                rank = tieEnd + 1;
            }
            return percentiles;
        }

        var fwhmPct  = RankPercentile(frames.Select(f => f.Metrics.Fwhm).ToArray(),              lowerIsBetter: true);
        var eccPct   = RankPercentile(frames.Select(f => f.Metrics.Eccentricity).ToArray(),       lowerIsBetter: true);
        var hfrPct   = RankPercentile(frames.Select(f => f.Metrics.Hfr).ToArray(),                lowerIsBetter: true);
        var starsPct = RankPercentile(frames.Select(f => (double)f.Metrics.StarCount).ToArray(),  lowerIsBetter: false);
        var bgPct    = RankPercentile(frames.Select(f => f.Metrics.MeanBackground).ToArray(),     lowerIsBetter: true);
        var trailPct = RankPercentile(frames.Select(f => (double)f.Metrics.SatelliteTrailConfidence).ToArray(), lowerIsBetter: true);

        for (var i = 0; i < frames.Count; i++)
        {
            var weighted = fwhmPct[i]  * fwhmWeight
                         + eccPct[i]   * eccWeight
                         + hfrPct[i]   * hfrWeight
                         + starsPct[i] * starsWeight
                         + bgPct[i]    * bgWeight
                         + trailPct[i] * trailWeight;

            frames[i].OverallScore = Math.Clamp((weighted / totalWeight) * 5.0, 0.0, 5.0);
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

    private void InitializeThresholdsFromLoadedFrames()
    {
        if (Frames.Count == 0)
        {
            return;
        }

        _maxFwhm = Frames.Max(f => f.Metrics.Fwhm);
        _maxHfr = Frames.Max(f => f.Metrics.Hfr);
        _maxEccentricity = Frames.Max(f => f.Metrics.Eccentricity);
        _maxMeanBackground = Frames.Max(f => f.Metrics.MeanBackground);
        _minStars = Frames.Min(f => (double)f.Metrics.StarCount);

        var sqmValues = Frames
            .Where(f => f.Metrics.Sqm.HasValue)
            .Select(f => f.Metrics.Sqm!.Value)
            .ToList();
        _minSqm = sqmValues.Count > 0 ? sqmValues.Min() : 0.0;

        var skyTempValues = Frames
            .Where(f => f.Metrics.SkyTemp.HasValue)
            .Select(f => f.Metrics.SkyTemp!.Value)
            .ToList();
        _maxSkyTemp = skyTempValues.Count > 0 ? skyTempValues.Max() : 40.0;

        OnPropertyChanged(nameof(MaxFwhm));
        OnPropertyChanged(nameof(MaxHfr));
        OnPropertyChanged(nameof(MaxEccentricity));
        OnPropertyChanged(nameof(MaxMeanBackground));
        OnPropertyChanged(nameof(MinStars));
        OnPropertyChanged(nameof(MinSqm));
        OnPropertyChanged(nameof(MaxSkyTemp));
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
            var (targetWidth, targetHeight) = GetInteractivePreviewDimensions(loaded);
            var materialized = await MaterializeFrameAsync(loaded, cancellationToken);
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

    private static (int Width, int Height) GetInteractivePreviewDimensions(LoadedFrameContext frame)
    {
        var longestSide = Math.Max(frame.Width, frame.Height);
        if (longestSide <= PreviewInteractiveMaxLongSide)
        {
            return (frame.Width, frame.Height);
        }

        var scale = PreviewInteractiveMaxLongSide / (double)longestSide;
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        return (width, height);
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

            for (var i = 0; i < _loadedFrames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var loaded = _loadedFrames[i];
                Status = $"Applying stretch ({i + 1}/{_loadedFrames.Count})";

                var frameData = await MaterializeFrameAsync(loaded, cancellationToken);
                var previews = await _rustafits.RenderPreviewBitmapsAsync(frameData, GetStfForFrame(frameData), _manualRoiRect, loaded.Item.Metrics, cancellationToken);

                loaded.Item.ThumbnailImage = previews.Full;
                loaded.Item.RoiImage = previews.Roi;

                ProgressValue = i + 1;
            }

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
            RefreshPreviewVisibleFrames);
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
        ScheduleThumbnailRebuild(immediate: true);
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
        var centerIndex = _loadedFrames.FindIndex(f => f.Item == centerItem);
        if (centerIndex < 0)
        {
            return;
        }

        TrimFullImageCache(centerIndex, ahead, behind);
        PublishPreviewCacheState();

        try
        {
            for (var i = 1; i <= ahead; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idx = centerIndex + i;
                if (idx >= _loadedFrames.Count)
                {
                    break;
                }

                await EnsureFullImageCachedAsync(idx, cancellationToken);
            }

            for (var i = 1; i <= behind; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idx = centerIndex - i;
                if (idx < 0)
                {
                    break;
                }

                await EnsureFullImageCachedAsync(idx, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnsureFullImageCachedAsync(int index, CancellationToken cancellationToken)
    {
        var loaded = _loadedFrames[index];
        if (loaded.FullImage is not null)
        {
            return;
        }

        var materializedCached = await MaterializeFrameAsync(loaded, cancellationToken);
        var full = await _rustafits.RenderFullBitmapAsync(materializedCached, GetStfForFrame(materializedCached), cancellationToken);
        _loadedFrames[index] = loaded with { FullImage = full };
        PublishPreviewCacheState();
    }

    private void ApplyThresholds()
    {
        var thresholds = new Thresholds
        {
            MaxFwhm = MaxFwhm,
            MinSqm = MinSqm,
            MaxSkyTemp = MaxSkyTemp,
            MaxHfr = MaxHfr,
            MaxEccentricity = MaxEccentricity,
            MaxMeanBackground = MaxMeanBackground,
            MinStars = MinStars,
            MinSatelliteConfidence = RejectSatelliteTrail ? MinSatelliteConfidence : 0,
            MinScore = MinScore
        };

        foreach (var frame in Frames)
        {
            var autoRejected = _rejection.ShouldReject(frame, thresholds);
            var reasons = autoRejected ? _rejection.GetRejectionReasons(frame, thresholds) : [];
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
            var moved = _move.MoveRejected(Frames, RejectedFolder, filterKeys);
            Status = $"Moved {moved} rejected frame(s).";
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
        var center = _rustafits.DetectRoiNormalizedCenter(await MaterializeFrameAsync(_loadedFrames[0], cancellationToken));
        var frame = _loadedFrames[0];
        var longest = Math.Max(frame.Width, frame.Height);
        var size = longest > 0 ? 160.0 / longest : 0.25;
        var left = Math.Clamp(center.X - size / 2, 0, 1 - size);
        var top = Math.Clamp(center.Y - size / 2, 0, 1 - size);
        _manualRoiRect = (left, top, size, size);
    }

    private static LoadedFrameContext CreateLoadedFrameContext(FrameItem item, RustafitsService.LoadedFrame frame, string filePath, bool rotate180)
    {
        return new LoadedFrameContext(
            item,
            filePath,
            frame.Width,
            frame.Height,
            frame.NormalizationMax,
            rotate180,
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
        return _rustafits.ApplyOrientation(raw, context.Rotate180);
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

        return (ahead, behind);
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

    private void TrimFullImageCache(int centerIndex, int ahead, int behind)
    {
        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            if (i >= centerIndex - behind && i <= centerIndex + ahead)
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
            var fallbackLoadedIndex = visibleFrameIndices[0];
            var fallbackItem = _loadedFrames[fallbackLoadedIndex].Item;
            _ = OpenPreviewAsync(fallbackItem);
            return;
        }

        _previewVm.UpdateFramePosition(currentVisibleIndex, visibleFrameIndices.Count);
        PublishPreviewCacheState();
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
}
