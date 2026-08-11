using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Rejector.Avalonia.Infrastructure;
using Rejector.Core.Models;
using Rejector.Core.Services;
using System.Net.Http;

namespace Rejector.Avalonia.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly AppSettingsService _appSettingsService = new();
    private readonly FrameDiscoveryService _discoveryService = new();
    private readonly FrameMoveService _moveService = new();
    private readonly FrameRejectionService _rejectionService = new();
    private readonly RustafitsService _analysisService = new();
    private readonly SessionService _sessionService = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly RelayCommand _analyzeCommand;
    private readonly RelayCommand _applyThresholdsCommand;
    private readonly RelayCommand _moveRejectedCommand;
    private readonly RelayCommand _toggleRejectCommand;
    private readonly RelayCommand _markSelectedKeepCommand;
    private readonly RelayCommand _markSelectedRejectedCommand;
    private readonly RelayCommand _clearSelectedOverrideCommand;
    private readonly RelayCommand _addSortRuleCommand;
    private readonly RelayCommand _removeSortRuleCommand;
    private readonly RelayCommand _resetThresholdsCommand;
    private readonly RelayCommand _createProfileCommand;
    private readonly RelayCommand _dismissUpdateBannerCommand;
    private readonly RelayCommand _showDebugUpdateBannerCommand;
    private readonly List<FrameResultContext> _resultContexts = [];
    private readonly HashSet<string> _cachedPreviewPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watchReloadGate = new();
    private readonly Dictionary<string, Thresholds> _filterThresholds = new(StringComparer.OrdinalIgnoreCase);
    private string _inputFolder = string.Empty;
    private string _rejectedFolder = string.Empty;
    private string _statusText = "Enter a folder path to analyze frames with the shared core.";
    private bool _includeSubfolders;
    private bool _watchFolderEnabled;
    private bool _isWatchingFolder;
    private bool _isSettingsOpen;
    private bool _isFolderPanelOpen;
    private bool _isAnalyzing;
    private bool _showAccepted = true;
    private bool _showRejected = true;
    private FrameSummaryViewModel? _selectedResult;
    private Bitmap? _selectedPreviewImage;
    private RustafitsService.RenderedImage? _selectedPreviewRenderedImage;
    public sealed record LoupeSample(
        int PixelX,
        int PixelY,
        int Width,
        int Height,
        byte[] Pixels,
        byte CenterValue,
        byte MinValue,
        byte MaxValue,
        double MeanValue);
    private string _selectedPreviewCaption = "Select a frame to preview it here.";
    private Thresholds _thresholds = new();
    private string _sortField = "File name";
    private string _newProfileName = "Default";
    private string _defaultProfileName = "Default";
    private SettingsProfile? _selectedSettingsProfile;
    private bool _isApplyingProfile;
    private double? _sessionFocalLengthMm;
    private double? _sessionPixelSizeUm;
    private bool _isUpdateBannerVisible;
    private string _updateBannerText = string.Empty;
    private string _performanceText = "Idle";
    private string _bottomStatusText = "Ready";
    private TimeSpan? _lastAnalysisElapsed;
    private int _lastAnalysisFrameCount;
    private double _lastAnalysisReadGibPerSecond;
    private bool _showFwhmMetric = true;
    private bool _showFwhmArcsecMetric = true;
    private bool _showHfrMetric = true;
    private bool _showStarsMetric = true;
    private bool _showEccentricityMetric = true;
    private bool _showTrailMetric = true;
    private bool _showSqmMetric = true;
    private bool _showSkyTempMetric = true;
    private bool _showMeanBackgroundMetric = true;
    private bool _showScoreMetric = true;
    private double _stfTargetBackground = 0.25;
    private double _stfShadows;
    private double _stfMidtones = 0.25;
    private double _stfHighlights = 1.0;
    private bool _useAutoStretchForPreview = true;
    private bool _suppressStfPreviewRefresh;
    private bool _isRoiOverlayVisible;
    private bool _isStarDebugOverlayVisible;
    private bool _isOrientationDebugOverlayVisible;
    private bool _isCurvatureViewVisible;
    private bool _isAlignmentEnabled = true;
    private bool _useScoreFwhm = true;
    private bool _useScoreHfr = true;
    private bool _useScoreStars = true;
    private bool _useScoreEccentricity = true;
    private bool _useScoreBackground = true;
    private bool _useScoreTrail = true;
    private double _scoreWeightFwhm = 3.0;
    private double _scoreWeightHfr = 1.5;
    private double _scoreWeightStars = 1.5;
    private double _scoreWeightEccentricity = 2.5;
    private double _scoreWeightBackground = 0.5;
    private double _scoreWeightTrail = 2.0;
    private double _previewFrameSliderValue;
    private bool _isSynchronizingPreviewSlider;
    private const string AllFiltersScopeLabel = "All Filters";
    private string _selectedRejectionFilter = AllFiltersScopeLabel;
    private List<FileSystemWatcher>? _folderWatchers;
    private CancellationTokenSource? _watchReloadCts;
    private (double Left, double Top, double Width, double Height) _manualRoi = (0.35, 0.35, 0.3, 0.3);

    public MainWindowViewModel()
    {
        var settings = _appSettingsService.Load();
        _inputFolder = settings.InputFolder ?? string.Empty;
        _rejectedFolder = settings.RejectedFolder ?? string.Empty;
        _includeSubfolders = settings.IncludeSubfolders;
        _watchFolderEnabled = settings.WatchFolder;
        _thresholds = settings.Profiles.FirstOrDefault()?.Thresholds?.Clone() ?? new Thresholds();
        _analyzeCommand = new RelayCommand(StartAnalyze, () => !_isAnalyzing && !string.IsNullOrWhiteSpace(InputFolder));
        _applyThresholdsCommand = new RelayCommand(ApplyThresholds, () => Results.Count > 0 && !_isAnalyzing);
        _moveRejectedCommand = new RelayCommand(StartMoveRejected, () => Results.Any(result => result.IsRejected) && !_isAnalyzing && !string.IsNullOrWhiteSpace(RejectedFolder));
        _toggleRejectCommand = new RelayCommand(parameter => ToggleReject(parameter as FrameSummaryViewModel), _ => !_isAnalyzing);
        _markSelectedKeepCommand = new RelayCommand(() => SetSelectedManualOverride(false), () => SelectedResult is not null && !_isAnalyzing);
        _markSelectedRejectedCommand = new RelayCommand(() => SetSelectedManualOverride(true), () => SelectedResult is not null && !_isAnalyzing);
        _clearSelectedOverrideCommand = new RelayCommand(ClearSelectedManualOverride, () => SelectedResult is not null && !_isAnalyzing);
        _addSortRuleCommand = new RelayCommand(AddSortRule, () => SortRules.Count < 4);
        _removeSortRuleCommand = new RelayCommand(parameter => RemoveSortRule(parameter as SortRuleViewModel), parameter => parameter is SortRuleViewModel && SortRules.Count > 1);
        _resetThresholdsCommand = new RelayCommand(ResetThresholds, () => _resultContexts.Count > 0 && !_isAnalyzing);
        _createProfileCommand = new RelayCommand(CreateSettingsProfile, CanCreateSettingsProfile);
        _dismissUpdateBannerCommand = new RelayCommand(() => IsUpdateBannerVisible = false, () => IsUpdateBannerVisible);
        _showDebugUpdateBannerCommand = new RelayCommand(async () =>
        {
            await ShowDebugUpdateBannerAsync();
        });

        SettingsProfiles.Clear();
        foreach (var profile in (settings.Profiles ?? []).Select(profile => profile.Clone()))
        {
            SettingsProfiles.Add(profile);
        }

        if (SettingsProfiles.Count == 0)
        {
            SettingsProfiles.Add(new SettingsProfile { Name = "Default" });
        }

        _defaultProfileName = SettingsProfile.NormalizeName(settings.DefaultProfileName);
        var defaultProfile = SettingsProfiles.FirstOrDefault(profile => string.Equals(profile.Name, _defaultProfileName, StringComparison.OrdinalIgnoreCase))
            ?? SettingsProfiles.First();
        SelectedSettingsProfile = defaultProfile;
        NewProfileName = defaultProfile.Name;

        SortRules.Add(new SortRuleViewModel(SortFieldOptions[0], true, RebuildResults));

        Dispatcher.UIThread.Post(() =>
        {
            _ = CheckForUpdateAsync();
        }, DispatcherPriority.Background);
    }

    private async Task CheckForUpdateAsync()
    {
        var update = await _updateCheckService.GetLatestUpdateAsync();
        if (update is not null)
        {
            UpdateBannerText = $"Update available: {update.Version} — {update.ReleaseNotesMarkdown}";
            IsUpdateBannerVisible = true;
        }
    }

    private async Task ShowDebugUpdateBannerAsync()
    {
        var update = await _updateCheckService.GetLatestReleaseInfoAsync();
        if (update is not null)
        {
            UpdateBannerText = $"Update available: {update.Version} — {update.ReleaseNotesMarkdown}";
            IsUpdateBannerVisible = true;
            return;
        }

        var fallbackVersion = "debug";
        UpdateBannerText = $"Update available: {fallbackVersion} — local parity validation build";
        IsUpdateBannerVisible = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => "Rejector";

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (_isSettingsOpen == value)
            {
                return;
            }

            _isSettingsOpen = value;
            OnPropertyChanged();
            if (value && _isFolderPanelOpen)
            {
                _isFolderPanelOpen = false;
                OnPropertyChanged(nameof(IsFolderPanelOpen));
            }
        }
    }

    public bool IsFolderPanelOpen
    {
        get => _isFolderPanelOpen;
        set
        {
            if (_isFolderPanelOpen == value)
            {
                return;
            }

            _isFolderPanelOpen = value;
            OnPropertyChanged();
            if (value && _isSettingsOpen)
            {
                _isSettingsOpen = false;
                OnPropertyChanged(nameof(IsSettingsOpen));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
            BottomStatusText = value;
        }
    }

    public string CurrentSlice => "Current slice: core extraction, headless analysis, desktop shell, portable render contract.";

    public string NextSlice => "Next slice: replace placeholder UI with real browse/analyze workflows and convert WPF view-model slices.";

    public string SettingsPath => _appSettingsService.SettingsFilePath;

    public string SessionFocalLengthText => _sessionFocalLengthMm is double value ? $"Focal: {value:F1} mm" : "Focal: n/a";

    public string SessionPixelSizeText => _sessionPixelSizeUm is double value ? $"Pixel: {value:F2} µm" : "Pixel: n/a";

    public string IncludeSubfoldersText => IncludeSubfolders ? "Subfolders: on" : "Subfolders: off";

    public string WatchFolderText => WatchFolderEnabled ? "Watch: on" : "Watch: off";

    public bool ShowAccepted
    {
        get => _showAccepted;
        set
        {
            if (_showAccepted == value)
            {
                return;
            }

            _showAccepted = value;
            OnPropertyChanged();
            RebuildResults();
        }
    }

    public bool ShowRejected
    {
        get => _showRejected;
        set
        {
            if (_showRejected == value)
            {
                return;
            }

            _showRejected = value;
            OnPropertyChanged();
            RebuildResults();
        }
    }

    public bool IsAlignmentEnabled
    {
        get => _isAlignmentEnabled;
        set
        {
            if (_isAlignmentEnabled == value)
            {
                return;
            }

            _isAlignmentEnabled = value;
            OnPropertyChanged();
            _cachedPreviewPaths.Clear();
            OnPropertyChanged(nameof(CachedPreviewCount));
            if (SelectedResult is not null)
            {
                StartLoadSelectedPreview();
            }
        }
    }

    public string InputFolder
    {
        get => _inputFolder;
        set
        {
            if (_inputFolder == value)
            {
                return;
            }

            _inputFolder = value;
            OnPropertyChanged();
            _analyzeCommand.RaiseCanExecuteChanged();
            PersistFolderSettings();
            RestartFolderWatchIfEnabled();
        }
    }

    public string RejectedFolder
    {
        get => _rejectedFolder;
        set
        {
            if (_rejectedFolder == value)
            {
                return;
            }

            _rejectedFolder = value;
            OnPropertyChanged();
            _moveRejectedCommand.RaiseCanExecuteChanged();
            PersistFolderSettings();
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value)
            {
                return;
            }

            _includeSubfolders = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IncludeSubfoldersText));
            PersistFolderSettings();
            PersistProfileSettings();
            RestartFolderWatchIfEnabled();
        }
    }

    public bool WatchFolderEnabled
    {
        get => _watchFolderEnabled;
        set
        {
            if (_watchFolderEnabled == value)
            {
                return;
            }

            _watchFolderEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WatchFolderText));
            OnPropertyChanged(nameof(AnalyzeButtonText));
            PersistFolderSettings();
            PersistProfileSettings();

            if (!value)
            {
                StopFolderWatch();
            }
            else
            {
                RestartFolderWatchIfEnabled();
            }
        }
    }

    public bool IsWatchingFolder
    {
        get => _isWatchingFolder;
        private set
        {
            if (_isWatchingFolder == value)
            {
                return;
            }

            _isWatchingFolder = value;
            OnPropertyChanged();
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (_isAnalyzing == value)
            {
                return;
            }

            _isAnalyzing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnalyzeButtonText));
            _analyzeCommand.RaiseCanExecuteChanged();
            _applyThresholdsCommand.RaiseCanExecuteChanged();
            _moveRejectedCommand.RaiseCanExecuteChanged();
            _resetThresholdsCommand.RaiseCanExecuteChanged();
            _createProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public string AnalyzeButtonText => IsAnalyzing
        ? "Loading..."
        : (WatchFolderEnabled ? "Load Frames & Watch Folder" : "Load Frames");

    public ObservableCollection<FrameSummaryViewModel> Results { get; } = [];

    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = [];

    public ObservableCollection<SettingsProfile> SettingsProfiles { get; } = [];

    public ObservableCollection<SortRuleViewModel> SortRules { get; } = [];

    public bool HasFilterChips => FilterChips.Count > 0;

    public bool HasMultipleFilterChips => FilterChips.Count > 1;

    public IReadOnlyList<string> RejectionFilterOptions =>
        [AllFiltersScopeLabel, .. FilterChips.Select(chip => chip.Key)];

    public string SelectedRejectionFilter
    {
        get => _selectedRejectionFilter;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? AllFiltersScopeLabel : value;
            if (string.Equals(_selectedRejectionFilter, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedRejectionFilter = next;
            OnPropertyChanged();
            RaiseAllThresholdPropertiesChanged();
            RaiseThresholdPanelDiagnosticsChanged();
        }
    }

    public string ResultCountText => $"{Results.Count} analyzed frame(s), {Results.Count(result => result.IsRejected)} rejected";

    public int TotalFrameCount => GetFrameStatistics().Total;

    public int ApprovedFrameCount => GetFrameStatistics().Accepted;

    public int RejectedFrameCount => GetFrameStatistics().Rejected;

    public double OverallAcceptedRatio => GetFrameStatistics().AcceptedRatio;

    public string ApprovedFramePercentageText => TotalFrameCount == 0 ? "0%" : $"{(ApprovedFrameCount * 100.0 / TotalFrameCount):F0}%";

    public string RejectedFramePercentageText => TotalFrameCount == 0 ? "0%" : $"{(RejectedFrameCount * 100.0 / TotalFrameCount):F0}%";

    public string AcceptedIntegrationTimeText => FormatIntegrationTime(GetFrameStatistics().AcceptedExposureSeconds);

    public string TotalIntegrationTimeText => FormatIntegrationTime(GetFrameStatistics().TotalExposureSeconds);

    public IReadOnlyList<FilterSummaryViewModel> FilterSummaries => GetFrameStatistics().Filters
        .Select(filter => new FilterSummaryViewModel(
            filter.FilterName,
            filter.Accepted,
            filter.Rejected,
            filter.Total,
            FormatIntegrationTime(filter.AcceptedExposureSeconds > 0
                ? filter.AcceptedExposureSeconds
                : filter.TotalExposureSeconds)))
        .ToList();

    public ICommand AnalyzeCommand => _analyzeCommand;

    public ICommand ApplyThresholdsCommand => _applyThresholdsCommand;

    public ICommand MoveRejectedCommand => _moveRejectedCommand;
    public ICommand ToggleRejectCommand => _toggleRejectCommand;
    public ICommand MarkSelectedKeepCommand => _markSelectedKeepCommand;
    public ICommand MarkSelectedRejectedCommand => _markSelectedRejectedCommand;
    public ICommand ClearSelectedOverrideCommand => _clearSelectedOverrideCommand;
    public ICommand AddSortRuleCommand => _addSortRuleCommand;
    public ICommand RemoveSortRuleCommand => _removeSortRuleCommand;
    public ICommand CreateProfileCommand => _createProfileCommand;
    public ICommand ResetThresholdsCommand => _resetThresholdsCommand;
    public ICommand DismissUpdateBannerCommand => _dismissUpdateBannerCommand;
    public ICommand DebugShowUpdateBannerCommand => _showDebugUpdateBannerCommand;

    public bool IsUpdateBannerVisible
    {
        get => _isUpdateBannerVisible;
        set
        {
            if (_isUpdateBannerVisible == value)
            {
                return;
            }

            _isUpdateBannerVisible = value;
            OnPropertyChanged();
            _dismissUpdateBannerCommand.RaiseCanExecuteChanged();
        }
    }

    public string UpdateBannerText
    {
        get => _updateBannerText;
        private set
        {
            if (_updateBannerText == value)
            {
                return;
            }

            _updateBannerText = value;
            OnPropertyChanged();
        }
    }

    public string PerformanceText
    {
        get => _performanceText;
        private set
        {
            if (_performanceText == value)
            {
                return;
            }

            _performanceText = value;
            OnPropertyChanged();
        }
    }

    public string BottomStatusText
    {
        get => _bottomStatusText;
        private set
        {
            if (_bottomStatusText == value)
            {
                return;
            }

            _bottomStatusText = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFwhmMetric { get => _showFwhmMetric; set { if (SetBool(ref _showFwhmMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowFwhmArcsecMetric { get => _showFwhmArcsecMetric; set { if (SetBool(ref _showFwhmArcsecMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowHfrMetric { get => _showHfrMetric; set { if (SetBool(ref _showHfrMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowStarsMetric { get => _showStarsMetric; set { if (SetBool(ref _showStarsMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowEccentricityMetric { get => _showEccentricityMetric; set { if (SetBool(ref _showEccentricityMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowTrailMetric { get => _showTrailMetric; set { if (SetBool(ref _showTrailMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowSqmMetric { get => _showSqmMetric; set { if (SetBool(ref _showSqmMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowSkyTempMetric { get => _showSkyTempMetric; set { if (SetBool(ref _showSkyTempMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowMeanBackgroundMetric { get => _showMeanBackgroundMetric; set { if (SetBool(ref _showMeanBackgroundMetric, value)) { PersistProfileSettings(); } } }
    public bool ShowScoreMetric { get => _showScoreMetric; set { if (SetBool(ref _showScoreMetric, value)) { PersistProfileSettings(); } } }

    public bool AutoStretchPerFrame
    {
        get => _useAutoStretchForPreview;
        set
        {
            if (_useAutoStretchForPreview == value)
            {
                return;
            }

            _useAutoStretchForPreview = value;
            OnPropertyChanged();
            if (SelectedResult is not null)
            {
                StartLoadSelectedPreview();
            }
        }
    }

    public double StfTargetBackground
    {
        get => _stfTargetBackground;
        set
        {
            var clamped = Math.Clamp(value, 0.01, 0.5);
            if (Math.Abs(_stfTargetBackground - clamped) < 0.0001)
            {
                return;
            }

            _stfTargetBackground = clamped;
            OnPropertyChanged();
            PersistProfileSettings();

            if (_useAutoStretchForPreview && SelectedResult is not null)
            {
                StartLoadSelectedPreview();
            }
        }
    }
    public double StfShadows { get => _stfShadows; set => SetStfAndRefresh(ref _stfShadows, value, 0.0, 1.0); }
    public double StfMidtones { get => _stfMidtones; set => SetStfAndRefresh(ref _stfMidtones, value, 0.0, 1.0); }
    public double StfHighlights { get => _stfHighlights; set => SetStfAndRefresh(ref _stfHighlights, value, 0.0, 1.0); }
    public bool IsRoiOverlayVisible { get => _isRoiOverlayVisible; set => SetBool(ref _isRoiOverlayVisible, value); }
    public bool IsStarDebugOverlayVisible { get => _isStarDebugOverlayVisible; set => SetBool(ref _isStarDebugOverlayVisible, value); }
    public bool IsOrientationDebugOverlayVisible { get => _isOrientationDebugOverlayVisible; set => SetBool(ref _isOrientationDebugOverlayVisible, value); }
    public bool IsCurvatureViewVisible { get => _isCurvatureViewVisible; set => SetBool(ref _isCurvatureViewVisible, value); }

    public bool UseScoreFwhm { get => _useScoreFwhm; set { if (SetBoolAndRebuild(ref _useScoreFwhm, value)) { PersistProfileSettings(); } } }
    public bool UseScoreHfr { get => _useScoreHfr; set { if (SetBoolAndRebuild(ref _useScoreHfr, value)) { PersistProfileSettings(); } } }
    public bool UseScoreStars { get => _useScoreStars; set { if (SetBoolAndRebuild(ref _useScoreStars, value)) { PersistProfileSettings(); } } }
    public bool UseScoreEccentricity { get => _useScoreEccentricity; set { if (SetBoolAndRebuild(ref _useScoreEccentricity, value)) { PersistProfileSettings(); } } }
    public bool UseScoreBackground { get => _useScoreBackground; set { if (SetBoolAndRebuild(ref _useScoreBackground, value)) { PersistProfileSettings(); } } }
    public bool UseScoreTrail { get => _useScoreTrail; set { if (SetBoolAndRebuild(ref _useScoreTrail, value)) { PersistProfileSettings(); } } }

    public double ScoreWeightFwhm { get => _scoreWeightFwhm; set { if (SetDoubleAndRebuild(ref _scoreWeightFwhm, value)) { PersistProfileSettings(); } } }
    public double ScoreWeightHfr { get => _scoreWeightHfr; set { if (SetDoubleAndRebuild(ref _scoreWeightHfr, value)) { PersistProfileSettings(); } } }
    public double ScoreWeightStars { get => _scoreWeightStars; set { if (SetDoubleAndRebuild(ref _scoreWeightStars, value)) { PersistProfileSettings(); } } }
    public double ScoreWeightEccentricity { get => _scoreWeightEccentricity; set { if (SetDoubleAndRebuild(ref _scoreWeightEccentricity, value)) { PersistProfileSettings(); } } }
    public double ScoreWeightBackground { get => _scoreWeightBackground; set { if (SetDoubleAndRebuild(ref _scoreWeightBackground, value)) { PersistProfileSettings(); } } }
    public double ScoreWeightTrail { get => _scoreWeightTrail; set { if (SetDoubleAndRebuild(ref _scoreWeightTrail, value)) { PersistProfileSettings(); } } }

    public int CachedPreviewCount => _cachedPreviewPaths.Count;

    public (double Left, double Top, double Width, double Height) CurrentManualRoi => _manualRoi;

    public void SetManualRoi((double Left, double Top, double Width, double Height) roi)
    {
        var width = Math.Clamp(roi.Width, 0.005, 1.0);
        var height = Math.Clamp(roi.Height, 0.005, 1.0);
        var next = (
            Left: Math.Clamp(roi.Left, 0.0, Math.Max(0.0, 1.0 - width)),
            Top: Math.Clamp(roi.Top, 0.0, Math.Max(0.0, 1.0 - height)),
            Width: width,
            Height: height);

        if (_manualRoi == next)
        {
            return;
        }

        _manualRoi = next;
        OnPropertyChanged(nameof(CurrentManualRoi));
        if (!IsAnalyzing)
        {
            BottomStatusText = $"ROI: {next.Left:F3}, {next.Top:F3}, {next.Width:F3} x {next.Height:F3}";
        }
    }

    public double PreviewFrameSliderValue
    {
        get => _previewFrameSliderValue;
        set
        {
            var clamped = Math.Clamp(value, 0.0, PreviewFrameSliderMaximum);
            if (Math.Abs(_previewFrameSliderValue - clamped) < 0.0001)
            {
                return;
            }

            _previewFrameSliderValue = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewFramePositionText));

            if (_isSynchronizingPreviewSlider)
            {
                return;
            }

            if (Results.Count == 0)
            {
                return;
            }

            var index = Math.Clamp((int)Math.Round(clamped), 0, Results.Count - 1);
            SelectedResult = Results[index];
        }
    }

    public double PreviewFrameSliderMaximum => Math.Max(0, Results.Count - 1);

    public string PreviewFramePositionText
    {
        get
        {
            if (Results.Count == 0)
            {
                return "0/0";
            }

            return $"{Math.Clamp((int)Math.Round(PreviewFrameSliderValue) + 1, 1, Results.Count)}/{Results.Count}";
        }
    }
    public string SelectedDecisionText
    {
        get
        {
            if (SelectedResult is null)
            {
                return "No frame selected";
            }

            var context = GetSelectedContext();
            if (context is null)
            {
                return SelectedResult.IsRejected ? "Rejected" : "Keep";
            }

            return context.Frame.ManualRejectedOverride switch
            {
                true => "Manual reject",
                false => "Manual keep",
                null when context.Frame.AutomaticRejected => "Auto reject",
                _ => "Auto keep",
            };
        }
    }

    public IReadOnlyList<string> SortFieldOptions { get; } = [
        "File name",
        "Score",
        "Observation date",
        "FWHM",
        "FWHM arcsec",
        "SQM",
        "Sky temp",
        "HFR",
        "Stars",
        "Eccentricity",
        "Mean background",
        "Median",
        "MAD",
        "Min",
        "Min count",
        "Max",
        "Max count"
    ];

    public Bitmap DemoPreview { get; } = CreateDemoPreview();

    public string SortField
    {
        get => _sortField;
        set
        {
            if (_sortField == value)
            {
                return;
            }

            _sortField = value;
            if (SortRules.Count > 0)
            {
                SortRules[0].Field = value;
            }
            OnPropertyChanged();
            RebuildResults();
        }
    }

    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (_newProfileName == value)
            {
                return;
            }

            _newProfileName = value;
            OnPropertyChanged();
            _createProfileCommand.RaiseCanExecuteChanged();
        }
    }

    public string DefaultProfileName
    {
        get => _defaultProfileName;
        set
        {
            var normalized = SettingsProfile.NormalizeName(value);
            if (_defaultProfileName == normalized)
            {
                return;
            }

            _defaultProfileName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectedProfileDefault));
            PersistProfileSettings();
        }
    }

    public SettingsProfile? SelectedSettingsProfile
    {
        get => _selectedSettingsProfile;
        set
        {
            if (ReferenceEquals(_selectedSettingsProfile, value))
            {
                return;
            }

            _selectedSettingsProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProfileName));
            OnPropertyChanged(nameof(IsSelectedProfileDefault));
            if (value is null || _isApplyingProfile)
            {
                return;
            }

            ApplySelectedProfileSettings(value);
        }
    }

    public string SelectedProfileName
    {
        get => SelectedSettingsProfile?.Name ?? string.Empty;
        set
        {
            var target = SettingsProfiles.FirstOrDefault(profile => string.Equals(profile.Name, value, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                SelectedSettingsProfile = target;
            }
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

    public FrameSummaryViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (ReferenceEquals(_selectedResult, value))
            {
                return;
            }

            _selectedResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDecisionText));
            _markSelectedKeepCommand.RaiseCanExecuteChanged();
            _markSelectedRejectedCommand.RaiseCanExecuteChanged();
            _clearSelectedOverrideCommand.RaiseCanExecuteChanged();
            SyncPreviewSliderFromSelection();
            StartLoadSelectedPreview();
        }
    }

    public Bitmap? SelectedPreviewImage
    {
        get => _selectedPreviewImage;
        private set
        {
            if (ReferenceEquals(_selectedPreviewImage, value))
            {
                return;
            }

            _selectedPreviewImage = value;
            OnPropertyChanged();
            _markSelectedKeepCommand.RaiseCanExecuteChanged();
            _markSelectedRejectedCommand.RaiseCanExecuteChanged();
            _clearSelectedOverrideCommand.RaiseCanExecuteChanged();
        }
    }

    public LoupeSample? BuildPreviewLoupeSample(int pixelX, int pixelY, int sampleSize = 31)
    {
        var source = _selectedPreviewRenderedImage;
        if (source is null || source.Width <= 0 || source.Height <= 0 || source.Rgb24Data.Length == 0)
        {
            return null;
        }

        pixelX = Math.Clamp(pixelX, 0, source.Width - 1);
        pixelY = Math.Clamp(pixelY, 0, source.Height - 1);
        var width = Math.Min(sampleSize, source.Width);
        var height = Math.Min(sampleSize, source.Height);
        var half = sampleSize / 2;
        var startX = Math.Clamp(pixelX - half, 0, Math.Max(0, source.Width - width));
        var startY = Math.Clamp(pixelY - half, 0, Math.Max(0, source.Height - height));
        var pixels = new byte[width * height];
        var minValue = byte.MaxValue;
        var maxValue = byte.MinValue;
        long sum = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = ((startY + y) * source.Stride) + ((startX + x) * 3);
                var red = source.Rgb24Data[sourceIndex];
                var green = source.Rgb24Data[sourceIndex + 1];
                var blue = source.Rgb24Data[sourceIndex + 2];
                var value = (byte)(((77 * red) + (150 * green) + (29 * blue) + 128) >> 8);
                pixels[(y * width) + x] = value;
                minValue = Math.Min(minValue, value);
                maxValue = Math.Max(maxValue, value);
                sum += value;
            }
        }

        var centerLocalX = Math.Clamp(pixelX - startX, 0, width - 1);
        var centerLocalY = Math.Clamp(pixelY - startY, 0, height - 1);
        return new LoupeSample(
            pixelX,
            pixelY,
            width,
            height,
            pixels,
            pixels[(centerLocalY * width) + centerLocalX],
            minValue,
            maxValue,
            sum / (double)Math.Max(1, pixels.Length));
    }

    private void SetSelectedManualOverride(bool rejected)
    {
        var context = GetSelectedContext();
        if (context is null)
        {
            return;
        }

        context.Frame.SetManualRejectedOverride(rejected);
        RebuildResults();
        StatusText = $"Set {context.Frame.FileName} to {(rejected ? "manual reject" : "manual keep")}.";
    }

    private void ClearSelectedManualOverride()
    {
        var context = GetSelectedContext();
        if (context is null)
        {
            return;
        }

        context.Frame.SetManualRejectedOverride(null);
        RebuildResults();
        StatusText = $"Cleared manual override for {context.Frame.FileName}.";
    }
    private FrameResultContext? GetSelectedContext()
    {
        var selected = SelectedResult;
        if (selected is null)
        {
            return null;
        }

        return _resultContexts.FirstOrDefault(item => string.Equals(item.Frame.FilePath, selected.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    public string SelectedPreviewCaption
    {
        get => _selectedPreviewCaption;
        private set
        {
            if (_selectedPreviewCaption == value)
            {
                return;
            }

            _selectedPreviewCaption = value;
            OnPropertyChanged();
        }
    }

    public void SetInputFolder(string path)
    {
        InputFolder = path;
    }

    public void SetRejectedFolder(string path)
    {
        RejectedFolder = path;
    }

    public double MaxFwhm
    {
        get => GetSelectedThresholds().MaxFwhm;
        set => SetThresholdValue(t => t.MaxFwhm, (t, v) => t.MaxFwhm = v, (t, v) => t.AutoCalcFwhmThreshold = v, value, nameof(MaxFwhm));
    }

    public double MaxHfr
    {
        get => GetSelectedThresholds().MaxHfr;
        set => SetThresholdValue(t => t.MaxHfr, (t, v) => t.MaxHfr = v, (t, v) => t.AutoCalcHfrThreshold = v, value, nameof(MaxHfr));
    }

    public double MaxFwhmArcsec
    {
        get => GetSelectedThresholds().MaxFwhmArcsec;
        set => SetThresholdValue(t => t.MaxFwhmArcsec, (t, v) => t.MaxFwhmArcsec = v, (t, v) => t.AutoCalcFwhmArcsecThreshold = v, value, nameof(MaxFwhmArcsec));
    }

    public double MinSqm
    {
        get => GetSelectedThresholds().MinSqm;
        set => SetThresholdValue(t => t.MinSqm, (t, v) => t.MinSqm = v, (t, v) => t.AutoCalcSqmThreshold = v, value, nameof(MinSqm));
    }

    public double MaxSkyTemp
    {
        get => GetSelectedThresholds().MaxSkyTemp;
        set => SetThresholdValue(t => t.MaxSkyTemp, (t, v) => t.MaxSkyTemp = v, (t, v) => t.AutoCalcSkyTempThreshold = v, value, nameof(MaxSkyTemp));
    }

    public double MaxEccentricity
    {
        get => GetSelectedThresholds().MaxEccentricity;
        set => SetThresholdValue(t => t.MaxEccentricity, (t, v) => t.MaxEccentricity = v, (t, v) => t.AutoCalcEccentricityThreshold = v, value, nameof(MaxEccentricity));
    }

    public double MaxMeanBackground
    {
        get => GetSelectedThresholds().MaxMeanBackground;
        set => SetThresholdValue(t => t.MaxMeanBackground, (t, v) => t.MaxMeanBackground = v, (t, v) => t.AutoCalcMeanBackgroundThreshold = v, value, nameof(MaxMeanBackground));
    }

    public double MinStars
    {
        get => GetSelectedThresholds().MinStars;
        set => SetThresholdValue(t => t.MinStars, (t, v) => t.MinStars = v, (t, v) => t.AutoCalcStarsThreshold = v, value, nameof(MinStars));
    }

    public int MinSatelliteConfidence
    {
        get => GetSelectedThresholds().MinSatelliteConfidence;
        set
        {
            var thresholds = GetSelectedThresholdsForEdit();
            if (thresholds.MinSatelliteConfidence == value)
            {
                return;
            }

            thresholds.MinSatelliteConfidence = value;
            thresholds.AutoCalcTrailThreshold = false;
            OnPropertyChanged();
            PersistSelectedThresholdScopeAndRevalidateAll();
        }
    }

    public double MinScore
    {
        get => GetSelectedThresholds().MinScore;
        set => SetThresholdValue(t => t.MinScore, (t, v) => t.MinScore = v, (t, v) => t.AutoCalcScoreThreshold = v, value, nameof(MinScore));
    }

    public int SatelliteTrailRejectedFrameCount => CountThresholdRejects(context =>
        GetSelectedThresholds().MinSatelliteConfidence > 0 && context.Frame.Metrics.SatelliteTrailConfidence >= GetSelectedThresholds().MinSatelliteConfidence);

    public int FwhmRejectedFrameCount => CountThresholdRejects(context => context.Frame.Metrics.Fwhm > GetSelectedThresholds().MaxFwhm);

    public int FwhmArcsecRejectedFrameCount => CountThresholdRejects(context =>
        context.Frame.Metrics.FwhmArcsec.HasValue && context.Frame.Metrics.FwhmArcsec.Value > GetSelectedThresholds().MaxFwhmArcsec);

    public int SqmRejectedFrameCount => CountThresholdRejects(context =>
        context.Frame.Metrics.Sqm.HasValue && context.Frame.Metrics.Sqm.Value < GetSelectedThresholds().MinSqm);

    public int SkyTempRejectedFrameCount => CountThresholdRejects(context =>
        context.Frame.Metrics.SkyTemp.HasValue && context.Frame.Metrics.SkyTemp.Value > GetSelectedThresholds().MaxSkyTemp);

    public int HfrRejectedFrameCount => CountThresholdRejects(context => context.Frame.Metrics.Hfr > GetSelectedThresholds().MaxHfr);

    public int EccentricityRejectedFrameCount => CountThresholdRejects(context => context.Frame.Metrics.Eccentricity > GetSelectedThresholds().MaxEccentricity);

    public int MeanBackgroundRejectedFrameCount => CountThresholdRejects(context => context.Frame.Metrics.MeanBackground > GetSelectedThresholds().MaxMeanBackground);

    public int StarCountRejectedFrameCount => CountThresholdRejects(context => context.Frame.Metrics.StarCount < GetSelectedThresholds().MinStars);

    public int ScoreRejectedFrameCount => CountThresholdRejects(context =>
        GetSelectedThresholds().MinScore > 0 && context.Frame.OverallScore < GetSelectedThresholds().MinScore);

    public async Task SaveSessionAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var session = new SessionData
        {
            SavedAt = DateTimeOffset.UtcNow,
            InputFolder = InputFolder,
            RejectedFolder = RejectedFolder,
            IncludeSubfolders = IncludeSubfolders,
            MaxFwhm = _thresholds.MaxFwhm,
            MaxFwhmArcsec = _thresholds.MaxFwhmArcsec,
            MaxHfr = _thresholds.MaxHfr,
            MaxEccentricity = _thresholds.MaxEccentricity,
            MaxMeanBackground = _thresholds.MaxMeanBackground,
            MinStars = _thresholds.MinStars,
            MinSqm = _thresholds.MinSqm,
            MaxSkyTemp = _thresholds.MaxSkyTemp,
            MinSatelliteConfidence = _thresholds.MinSatelliteConfidence,
            MinScore = _thresholds.MinScore,
            Frames = _resultContexts.Select(context => new SessionFrameEntry
            {
                FilePath = context.Frame.FilePath,
                FileName = context.Frame.FileName,
                RelativePath = context.Frame.RelativePath,
                AutoRejected = context.Frame.AutomaticRejected,
                ManualRejectedOverride = context.Frame.ManualRejectedOverride,
                OverallScore = context.Frame.OverallScore,
                Fwhm = context.Frame.Metrics.Fwhm,
                FwhmArcsec = context.Frame.Metrics.FwhmArcsec,
                Sqm = context.Frame.Metrics.Sqm,
                SkyTemp = context.Frame.Metrics.SkyTemp,
                Hfr = context.Frame.Metrics.Hfr,
                StarCount = context.Frame.Metrics.StarCount,
                Eccentricity = context.Frame.Metrics.Eccentricity,
                MeanBackground = context.Frame.Metrics.MeanBackground,
                Median = context.Frame.Metrics.Median,
                Mad = context.Frame.Metrics.Mad,
                Min = context.Frame.Metrics.Min,
                MinCount = context.Frame.Metrics.MinCount,
                Max = context.Frame.Metrics.Max,
                MaxCount = context.Frame.Metrics.MaxCount,
                SatelliteTrailConfidence = context.Frame.Metrics.SatelliteTrailConfidence,
                TrailX1 = context.Frame.Metrics.TrailX1,
                TrailY1 = context.Frame.Metrics.TrailY1,
                TrailX2 = context.Frame.Metrics.TrailX2,
                TrailY2 = context.Frame.Metrics.TrailY2,
                ExposureDateTime = context.Frame.ExposureDateTime,
                ExposureSeconds = context.Frame.ExposureSeconds,
                FilterName = context.Frame.FilterName,
                FocalLengthMm = context.Frame.Metrics.FocalLengthMm,
                PixelSizeUm = context.Frame.Metrics.PixelSizeUm,
                Width = context.Width,
                Height = context.Height,
                Rotate180 = context.Rotate180,
                ShiftX = context.ShiftX,
                ShiftY = context.ShiftY,
                NormalizationMax = context.NormalizationMax,
                ThumbnailPng = context.ThumbnailPayload,
                RoiPng = context.RoiPayload,
            }).ToList(),
        };

        _sessionService.Save(path, session);
        StatusText = $"Saved session with {session.Frames.Count} frame(s) to {path}";
    }

    public Task LoadSessionAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        var session = _sessionService.Load(path);
        if (session is null)
        {
            StatusText = $"Session file could not be loaded: {path}";
            return Task.CompletedTask;
        }

        Results.Clear();
        _resultContexts.Clear();
        FilterChips.Clear();
        _sessionFocalLengthMm = null;
        _sessionPixelSizeUm = null;

        InputFolder = session.InputFolder ?? string.Empty;
        RejectedFolder = session.RejectedFolder ?? string.Empty;
        IncludeSubfolders = session.IncludeSubfolders;
        _thresholds.MaxFwhm = session.MaxFwhm;
        _thresholds.MaxFwhmArcsec = session.MaxFwhmArcsec;
        _thresholds.MaxHfr = session.MaxHfr;
        _thresholds.MaxEccentricity = session.MaxEccentricity;
        _thresholds.MaxMeanBackground = session.MaxMeanBackground;
        _thresholds.MinStars = session.MinStars;
        _thresholds.MinSqm = session.MinSqm;
        _thresholds.MaxSkyTemp = session.MaxSkyTemp;
        _thresholds.MinSatelliteConfidence = session.MinSatelliteConfidence;
        _thresholds.MinScore = session.MinScore;
        OnPropertyChanged(nameof(MaxFwhm));
        OnPropertyChanged(nameof(MaxFwhmArcsec));
        OnPropertyChanged(nameof(MinSqm));
        OnPropertyChanged(nameof(MaxSkyTemp));
        OnPropertyChanged(nameof(MaxHfr));
        OnPropertyChanged(nameof(MaxEccentricity));
        OnPropertyChanged(nameof(MaxMeanBackground));
        OnPropertyChanged(nameof(MinStars));
        OnPropertyChanged(nameof(MinSatelliteConfidence));
        OnPropertyChanged(nameof(MinScore));
        RaiseThresholdPanelDiagnosticsChanged();

        foreach (var entry in session.Frames)
        {
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
                FocalLengthMm = entry.FocalLengthMm,
                PixelSizeUm = entry.PixelSizeUm,
                SatelliteTrailConfidence = entry.SatelliteTrailConfidence,
                TrailX1 = entry.TrailX1,
                TrailY1 = entry.TrailY1,
                TrailX2 = entry.TrailX2,
                TrailY2 = entry.TrailY2,
            };

            var frame = new ProcessedFrame
            {
                FilePath = entry.FilePath,
                FileName = entry.FileName,
                RelativePath = entry.RelativePath,
                ExposureDateTime = entry.ExposureDateTime,
                ExposureSeconds = entry.ExposureSeconds,
                FilterName = entry.FilterName,
                Metrics = metrics,
                OverallScore = entry.OverallScore,
            };

            frame.SetAutomaticRejected(entry.AutoRejected);
            frame.SetManualRejectedOverride(entry.ManualRejectedOverride);

            var thumbnail = PreviewPayloadCodec.DecodeToBitmap(entry.ThumbnailPng);
            var roiImage = PreviewPayloadCodec.DecodeToBitmap(entry.RoiPng);

            _resultContexts.Add(new FrameResultContext(
                frame,
                entry.Width,
                entry.Height,
                entry.NormalizationMax,
                entry.ThumbnailPng,
                entry.RoiPng,
                null,
                entry.Rotate180,
                entry.ShiftX,
                entry.ShiftY));
            Results.Add(CreateFrameSummary(_resultContexts[^1]));

            _sessionFocalLengthMm ??= entry.FocalLengthMm;
            _sessionPixelSizeUm ??= entry.PixelSizeUm;
        }

        RefreshFilterChips();
        ApplyThresholds();
        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(SessionFocalLengthText));
        OnPropertyChanged(nameof(SessionPixelSizeText));
        IsSettingsOpen = false;
        StatusText = $"Loaded session with {session.Frames.Count} frame(s) from {path}";
        return Task.CompletedTask;
    }

    public async Task AnalyzeAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        IsAnalyzing = true;
        Results.Clear();
        _resultContexts.Clear();
        FilterChips.Clear();
        _sessionFocalLengthMm = null;
        _sessionPixelSizeUm = null;
        SelectedResult = null;
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(SessionFocalLengthText));
        OnPropertyChanged(nameof(SessionPixelSizeText));

        try
        {
            StatusText = "Scanning folder...";
            var files = _discoveryService.Discover(InputFolder, IncludeSubfolders);
            var totalFiles = files.Count;
            var completedFiles = 0;
            long bytesRead = 0;
            StatusText = files.Count == 0
                ? (WatchFolderEnabled
                    ? "Watching folder for new FITS/XISF frames..."
                    : "No FITS/XISF files found in the selected folder.")
                : $"Discovered {files.Count} file(s). Running shared-core analysis...";

            if (files.Count > 0)
            {
                var indexedFiles = files.Select((file, index) => (FilePath: file, Index: index)).ToList();
                var prepared = new List<(int SourceIndex, FrameResultContext Context, double? FocalLengthMm, double? PixelSizeUm)>();
                RustafitsService.LoadedFrame? orientationReference = null;
                AstroMetrics? orientationReferenceMetrics = null;
                var skippedCount = 0;
                var firstLightFileIndex = -1;

                // Build the orientation reference frame first so downstream frame work can run in parallel.
                foreach (var candidate in indexedFiles)
                {
                    var raw = await _analysisService.LoadRawFrameAsync(candidate.FilePath, CancellationToken.None);
                    completedFiles++;
                    if (File.Exists(candidate.FilePath))
                    {
                        try
                        {
                            bytesRead += new FileInfo(candidate.FilePath).Length;
                        }
                        catch
                        {
                        }
                    }
                    StatusText = $"Loading frames {completedFiles}/{totalFiles}: {Path.GetFileName(candidate.FilePath)}";
                    if (!raw.IsLightFrame)
                    {
                        skippedCount++;
                        continue;
                    }

                    var metrics = _analysisService.AnalyzeFrame(raw);
                    var orientationDebug = _analysisService.CreateOrientationReferenceDebugInfo(raw, metrics);
                    var stf = _analysisService.ComputeAutoStretch(raw);
                    var roiRect = _analysisService.DetectRoiNormalizedRect(raw);
                    SetManualRoi(roiRect);
                    var previews = await _analysisService.RenderPreviewImagesAsync(raw, stf, roiRect, metrics, CancellationToken.None);

                    var frame = new ProcessedFrame
                    {
                        FilePath = candidate.FilePath,
                        FileName = Path.GetFileName(candidate.FilePath),
                        RelativePath = ComputeRelativePath(candidate.FilePath),
                        ExposureDateTime = raw.ExposureDateTime,
                        ExposureSeconds = raw.ExposureSeconds,
                        FilterName = raw.FilterName,
                        Metrics = metrics,
                    };
                    frame.SetAutomaticRejected(_rejectionService.ShouldReject(frame, ResolveThresholdsForFrame(frame)));

                    var thumbnailPayload = PreviewPayloadCodec.Encode(previews.Full);
                    var roiPayload = PreviewPayloadCodec.Encode(previews.Roi);
                    prepared.Add((
                        candidate.Index,
                        new FrameResultContext(frame, raw.Width, raw.Height, raw.NormalizationMax, thumbnailPayload, roiPayload, orientationDebug, false, 0, 0),
                        raw.FocalLengthMm,
                        raw.PixelSizeUm));

                    orientationReference = raw;
                    orientationReferenceMetrics = metrics;
                    firstLightFileIndex = candidate.Index;
                    break;
                }

                if (orientationReference is not null && orientationReferenceMetrics is not null)
                {
                    var maxParallelism = Math.Min(GetAnalyzeParallelism(), Math.Max(1, files.Count - 1));
                    using var gate = new SemaphoreSlim(Math.Max(1, maxParallelism));

                    var pending = indexedFiles
                        .Where(entry => entry.Index > firstLightFileIndex)
                        .Select(async entry =>
                        {
                            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                            try
                            {
                                var raw = await _analysisService.LoadRawFrameAsync(entry.FilePath, CancellationToken.None).ConfigureAwait(false);
                                long fileSize = 0;
                                if (File.Exists(entry.FilePath))
                                {
                                    try
                                    {
                                        fileSize = new FileInfo(entry.FilePath).Length;
                                    }
                                    catch
                                    {
                                    }
                                }
                                if (!raw.IsLightFrame)
                                {
                                    return (HasValue: false, SourceIndex: entry.Index, Context: (FrameResultContext?)null, Focal: (double?)null, Pixel: (double?)null, FileName: Path.GetFileName(entry.FilePath), FileSize: fileSize);
                                }

                                var metrics = _analysisService.AnalyzeFrame(raw);
                                var orientation = _analysisService.AnalyzeOrientation(raw, metrics, orientationReference, orientationReferenceMetrics);
                                var orientationDebug = orientation.CandidateDebug;
                                var stf = _analysisService.ComputeAutoStretch(raw);
                                var roiRect = _analysisService.DetectRoiNormalizedRect(raw);
                                var previews = await _analysisService.RenderPreviewImagesAsync(raw, stf, roiRect, metrics, CancellationToken.None).ConfigureAwait(false);

                                var frame = new ProcessedFrame
                                {
                                    FilePath = entry.FilePath,
                                    FileName = Path.GetFileName(entry.FilePath),
                                    RelativePath = ComputeRelativePath(entry.FilePath),
                                    ExposureDateTime = raw.ExposureDateTime,
                                    ExposureSeconds = raw.ExposureSeconds,
                                    FilterName = raw.FilterName,
                                    Metrics = metrics,
                                };
                                frame.SetAutomaticRejected(_rejectionService.ShouldReject(frame, ResolveThresholdsForFrame(frame)));

                                var thumbnailPayload = PreviewPayloadCodec.Encode(previews.Full);
                                var roiPayload = PreviewPayloadCodec.Encode(previews.Roi);
                                var context = new FrameResultContext(
                                    frame,
                                    raw.Width,
                                    raw.Height,
                                    raw.NormalizationMax,
                                    thumbnailPayload,
                                    roiPayload,
                                    orientationDebug,
                                    orientation.Rotate180,
                                    orientation.ShiftX,
                                    orientation.ShiftY);
                                return (HasValue: true, SourceIndex: entry.Index, Context: (FrameResultContext?)context, Focal: raw.FocalLengthMm, Pixel: raw.PixelSizeUm, FileName: frame.FileName, FileSize: fileSize);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        })
                        .ToList<Task<(bool HasValue, int SourceIndex, FrameResultContext? Context, double? Focal, double? Pixel, string FileName, long FileSize)>>();

                    while (pending.Count > 0)
                    {
                        var completedTask = await Task.WhenAny(pending);
                        pending.Remove(completedTask);
                        var item = await completedTask;

                        completedFiles++;
                        bytesRead += item.FileSize;
                        StatusText = $"Loading frames {completedFiles}/{totalFiles}: {item.FileName}";

                        if (!item.HasValue || item.Context is null)
                        {
                            skippedCount++;
                            continue;
                        }

                        prepared.Add((item.SourceIndex, item.Context, item.Focal, item.Pixel));
                    }
                }

                foreach (var item in prepared.OrderBy(item => item.SourceIndex))
                {
                    _resultContexts.Add(item.Context);
                    _sessionFocalLengthMm ??= item.FocalLengthMm;
                    _sessionPixelSizeUm ??= item.PixelSizeUm;
                }

                StatusText = "Finalizing frame comparisons...";
                RebuildResults();
                StatusText = "Building filter chips...";
                RefreshFilterChips();
                StatusText = "Initializing rejection thresholds...";
                RaiseAllThresholdPropertiesChanged();
                StatusText = "Applying rejection thresholds...";
                ApplyThresholds();
                SelectedResult = Results.FirstOrDefault();
                OnPropertyChanged(nameof(SessionFocalLengthText));
                OnPropertyChanged(nameof(SessionPixelSizeText));
                OnPropertyChanged(nameof(ResultCountText));

                if (_resultContexts.Count == 0)
                {
                    StatusText = skippedCount > 0
                        ? $"No light frames found ({skippedCount} file(s) skipped)."
                        : "No FITS/XISF files found in the selected folder.";
                }

                stopwatch.Stop();
                var elapsedSeconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                var gibRead = bytesRead / (1024.0 * 1024.0 * 1024.0);
                var gibPerSecond = gibRead / elapsedSeconds;

                if (_resultContexts.Count > 0)
                {
                    StatusText = skippedCount > 0
                        ? $"Analysis complete. {_resultContexts.Count} light frame(s) processed, {skippedCount} file(s) skipped. {elapsedSeconds:F1}s, {gibPerSecond:F2} GB/s read."
                        : $"Analysis complete. {_resultContexts.Count} frame(s) processed using the shared core. {elapsedSeconds:F1}s, {gibPerSecond:F2} GB/s read.";
                }
            }

            var settings = _appSettingsService.Load();
            settings.InputFolder = InputFolder;
            settings.RejectedFolder = RejectedFolder;
            settings.IncludeSubfolders = IncludeSubfolders;
            settings.WatchFolder = WatchFolderEnabled;
            if (settings.Profiles.Count == 0)
            {
                settings.Profiles.Add(new SettingsProfile { Name = "Default" });
            }
            settings.Profiles[0].Thresholds = CloneThresholds();
            _appSettingsService.Save(settings);

            if (Results.Count > 0)
            {
                IsFolderPanelOpen = false;
            }

            BottomStatusText = StatusText;
            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
            var perSecond = stopwatch.Elapsed.TotalSeconds <= 0.001
                ? _resultContexts.Count
                : _resultContexts.Count / stopwatch.Elapsed.TotalSeconds;
            _lastAnalysisElapsed = stopwatch.Elapsed;
            _lastAnalysisFrameCount = _resultContexts.Count;
            _lastAnalysisReadGibPerSecond = bytesRead / (1024.0 * 1024.0 * 1024.0) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            UpdatePerformanceText(perSecond);

            if (WatchFolderEnabled)
            {
                StartFolderWatch();
            }
            else
            {
                StopFolderWatch();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Analysis failed: {ex.Message}";
            BottomStatusText = StatusText;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async void StartAnalyze()
    {
        await AnalyzeAsync();
    }

    private static int GetAnalyzeParallelism()
    {
        var cores = Math.Max(2, Environment.ProcessorCount);
        var isAppleSilicon = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        // Apple Silicon can sustain more decode/render workers without UI starvation.
        if (isAppleSilicon)
        {
            return Math.Clamp((int)Math.Round(cores * 0.75), 4, 10);
        }

        return Math.Clamp(cores / 2, 2, 8);
    }

    private void PersistFolderSettings()
    {
        var settings = _appSettingsService.Load();
        settings.InputFolder = InputFolder;
        settings.RejectedFolder = RejectedFolder;
        settings.IncludeSubfolders = IncludeSubfolders;
        settings.WatchFolder = WatchFolderEnabled;
        _appSettingsService.Save(settings);
    }

    private bool CanCreateSettingsProfile()
    {
        var normalized = SettingsProfile.NormalizeName(NewProfileName);
        return !string.IsNullOrWhiteSpace(normalized)
            && !SettingsProfiles.Any(profile => string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void CreateSettingsProfile()
    {
        var normalized = SettingsProfile.NormalizeName(NewProfileName);
        if (string.IsNullOrWhiteSpace(normalized) || SettingsProfiles.Any(profile => string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var profile = CreateProfileFromCurrentState(normalized);
        SettingsProfiles.Add(profile);
        NewProfileName = profile.Name;
        SelectedSettingsProfile = profile;
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
                    _filterThresholds[NormalizeFilterKey(filterThresholds.Key)] = filterThresholds.Thresholds?.Clone() ?? new Thresholds();
                }
            }

            _thresholds = profile.Thresholds?.Clone() ?? new Thresholds();
            IncludeSubfolders = profile.IncludeSubfoldersDefault;
            WatchFolderEnabled = profile.WatchFolderDefault;
            StfTargetBackground = profile.StfTargetBackgroundDefault;

            ShowFwhmMetric = profile.ShowFwhmMetric;
            ShowFwhmArcsecMetric = profile.ShowFwhmArcsecMetric;
            ShowHfrMetric = profile.ShowHfrMetric;
            ShowStarsMetric = profile.ShowStarsMetric;
            ShowEccentricityMetric = profile.ShowEccentricityMetric;
            ShowTrailMetric = profile.ShowTrailMetric;
            ShowSqmMetric = profile.ShowSqmMetric;
            ShowSkyTempMetric = profile.ShowSkyTempMetric;
            ShowMeanBackgroundMetric = profile.ShowMeanBackgroundMetric;
            ShowScoreMetric = profile.ShowScoreMetric;

            UseScoreFwhm = profile.UseScoreFwhm;
            UseScoreHfr = profile.UseScoreHfr;
            UseScoreStars = profile.UseScoreStars;
            UseScoreEccentricity = profile.UseScoreEccentricity;
            UseScoreBackground = profile.UseScoreMeanBackground;
            UseScoreTrail = profile.UseScoreTrail;
            ScoreWeightFwhm = profile.ScoreWeightFwhm;
            ScoreWeightHfr = profile.ScoreWeightHfr;
            ScoreWeightStars = profile.ScoreWeightStars;
            ScoreWeightEccentricity = profile.ScoreWeightEccentricity;
            ScoreWeightBackground = profile.ScoreWeightMeanBackground;
            ScoreWeightTrail = profile.ScoreWeightTrail;

            OnPropertyChanged(nameof(MaxFwhm));
            OnPropertyChanged(nameof(MaxHfr));
            OnPropertyChanged(nameof(MaxFwhmArcsec));
            OnPropertyChanged(nameof(MinSqm));
            OnPropertyChanged(nameof(MaxSkyTemp));
            OnPropertyChanged(nameof(MaxEccentricity));
            OnPropertyChanged(nameof(MaxMeanBackground));
            OnPropertyChanged(nameof(MinStars));
            OnPropertyChanged(nameof(MinSatelliteConfidence));
            OnPropertyChanged(nameof(MinScore));
            OnPropertyChanged(nameof(ShowFwhmMetric));
            OnPropertyChanged(nameof(ShowFwhmArcsecMetric));
            OnPropertyChanged(nameof(ShowHfrMetric));
            OnPropertyChanged(nameof(ShowStarsMetric));
            OnPropertyChanged(nameof(ShowEccentricityMetric));
            OnPropertyChanged(nameof(ShowTrailMetric));
            OnPropertyChanged(nameof(ShowSqmMetric));
            OnPropertyChanged(nameof(ShowSkyTempMetric));
            OnPropertyChanged(nameof(ShowMeanBackgroundMetric));
            OnPropertyChanged(nameof(ShowScoreMetric));
            OnPropertyChanged(nameof(UseScoreFwhm));
            OnPropertyChanged(nameof(UseScoreHfr));
            OnPropertyChanged(nameof(UseScoreStars));
            OnPropertyChanged(nameof(UseScoreEccentricity));
            OnPropertyChanged(nameof(UseScoreBackground));
            OnPropertyChanged(nameof(UseScoreTrail));
            OnPropertyChanged(nameof(ScoreWeightFwhm));
            OnPropertyChanged(nameof(ScoreWeightHfr));
            OnPropertyChanged(nameof(ScoreWeightStars));
            OnPropertyChanged(nameof(ScoreWeightEccentricity));
            OnPropertyChanged(nameof(ScoreWeightBackground));
            OnPropertyChanged(nameof(ScoreWeightTrail));
            OnPropertyChanged(nameof(IncludeSubfolders));
            OnPropertyChanged(nameof(WatchFolderEnabled));
            OnPropertyChanged(nameof(StfTargetBackground));
            ApplyThresholds(updateStatus: false);
        }
        finally
        {
            _isApplyingProfile = false;
        }

        PersistProfileSettings();
    }

    private void PersistProfileSettings()
    {
        if (_isApplyingProfile || SelectedSettingsProfile is null)
        {
            return;
        }

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

        var settings = _appSettingsService.Load();
        settings.InputFolder = InputFolder;
        settings.RejectedFolder = RejectedFolder;
        settings.IncludeSubfolders = IncludeSubfolders;
        settings.WatchFolder = WatchFolderEnabled;
        settings.DefaultProfileName = DefaultProfileName;
        settings.Profiles = SettingsProfiles.Select(profile => profile.Clone()).ToList();
        _appSettingsService.Save(settings);
    }

    private void OverrideSelectedProfileThresholdsAndRevalidateAll()
    {
        _filterThresholds.Clear();
        SelectedSettingsProfile?.OverrideThresholds(_thresholds);

        RevalidateAllFramesWithResolvedThresholds();
        PersistProfileSettings();
    }

    private void PersistSelectedThresholdScopeAndRevalidateAll()
    {
        if (string.Equals(SelectedRejectionFilter, AllFiltersScopeLabel, StringComparison.OrdinalIgnoreCase))
        {
            OverrideSelectedProfileThresholdsAndRevalidateAll();
            return;
        }

        RevalidateAllFramesWithResolvedThresholds();
        PersistProfileSettings();
    }

    private Thresholds GetSelectedThresholds()
    {
        if (string.Equals(SelectedRejectionFilter, AllFiltersScopeLabel, StringComparison.OrdinalIgnoreCase))
        {
            return _thresholds;
        }

        var key = NormalizeFilterKey(SelectedRejectionFilter);
        return _filterThresholds.TryGetValue(key, out var thresholds) ? thresholds : _thresholds;
    }

    private Thresholds GetSelectedThresholdsForEdit()
    {
        if (string.Equals(SelectedRejectionFilter, AllFiltersScopeLabel, StringComparison.OrdinalIgnoreCase))
        {
            return _thresholds;
        }

        var key = NormalizeFilterKey(SelectedRejectionFilter);
        if (!_filterThresholds.TryGetValue(key, out var thresholds))
        {
            thresholds = _thresholds.Clone();
            _filterThresholds[key] = thresholds;
        }

        return thresholds;
    }

    private Thresholds ResolveThresholdsForFrame(ProcessedFrame frame)
    {
        var key = NormalizeFilterKey(frame.FilterName);
        return _filterThresholds.TryGetValue(key, out var thresholds) ? thresholds : _thresholds;
    }

    private void RevalidateAllFramesWithResolvedThresholds()
    {
        if (!IsAnalyzing && _resultContexts.Count > 0)
        {
            _rejectionService.RevalidateAll(_resultContexts.Select(context => context.Frame), _thresholds, _filterThresholds);
            RebuildResults();
        }

        RaiseThresholdPanelDiagnosticsChanged();
    }

    private SettingsProfile CreateProfileFromCurrentState(string name)
    {
        return new SettingsProfile
        {
            Name = SettingsProfile.NormalizeName(name),
            Thresholds = _thresholds.Clone(),
            FilterThresholds = _filterThresholds.Select(kvp => new ProfileFilterThresholds
            {
                Key = kvp.Key,
                Thresholds = kvp.Value.Clone(),
            }).ToList(),
            IncludeSubfoldersDefault = IncludeSubfolders,
            WatchFolderDefault = WatchFolderEnabled,
            StfTargetBackgroundDefault = StfTargetBackground,
            ShowFwhmMetric = ShowFwhmMetric,
            ShowFwhmArcsecMetric = ShowFwhmArcsecMetric,
            ShowHfrMetric = ShowHfrMetric,
            ShowStarsMetric = ShowStarsMetric,
            ShowEccentricityMetric = ShowEccentricityMetric,
            ShowTrailMetric = ShowTrailMetric,
            ShowSqmMetric = ShowSqmMetric,
            ShowSkyTempMetric = ShowSkyTempMetric,
            ShowMeanBackgroundMetric = ShowMeanBackgroundMetric,
            ShowScoreMetric = ShowScoreMetric,
            UseScoreFwhm = UseScoreFwhm,
            UseScoreHfr = UseScoreHfr,
            UseScoreStars = UseScoreStars,
            UseScoreEccentricity = UseScoreEccentricity,
            UseScoreMeanBackground = UseScoreBackground,
            UseScoreTrail = UseScoreTrail,
            ScoreWeightFwhm = ScoreWeightFwhm,
            ScoreWeightHfr = ScoreWeightHfr,
            ScoreWeightStars = ScoreWeightStars,
            ScoreWeightEccentricity = ScoreWeightEccentricity,
            ScoreWeightMeanBackground = ScoreWeightBackground,
            ScoreWeightTrail = ScoreWeightTrail,
            AutoCalcTrailThreshold = _thresholds.AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = _thresholds.AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = _thresholds.AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = _thresholds.AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = _thresholds.AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = _thresholds.AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = _thresholds.AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = _thresholds.AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = _thresholds.AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = _thresholds.AutoCalcScoreThreshold,
        };
    }

    private void RestartFolderWatchIfEnabled()
    {
        if (IsAnalyzing)
        {
            return;
        }

        if (WatchFolderEnabled)
        {
            StartFolderWatch();
        }
        else
        {
            StopFolderWatch();
        }
    }

    private void StartFolderWatch()
    {
        StopFolderWatch();

        if (!WatchFolderEnabled || string.IsNullOrWhiteSpace(InputFolder))
        {
            return;
        }

        var folders = InputFolder.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0)
        {
            return;
        }

        _folderWatchers = [];
        foreach (var folder in folders)
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            foreach (var ext in new[] { "*.fit", "*.fits", "*.xisf" })
            {
                watcher.Filters.Add(ext);
            }

            watcher.Created += OnWatchFilesystemChanged;
            watcher.Deleted += OnWatchFilesystemChanged;
            watcher.Renamed += OnWatchFilesystemRenamed;
            watcher.Error += OnWatchFilesystemError;
            _folderWatchers.Add(watcher);
        }

        IsWatchingFolder = _folderWatchers.Count > 0;
        if (IsWatchingFolder)
        {
            BottomStatusText = $"Watching {folders.Count} folder(s) for new FITS/XISF frames.";
        }
    }

    private void StopFolderWatch()
    {
        lock (_watchReloadGate)
        {
            _watchReloadCts?.Cancel();
            _watchReloadCts?.Dispose();
            _watchReloadCts = null;
        }

        if (_folderWatchers is null)
        {
            IsWatchingFolder = false;
            return;
        }

        foreach (var watcher in _folderWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnWatchFilesystemChanged;
            watcher.Deleted -= OnWatchFilesystemChanged;
            watcher.Renamed -= OnWatchFilesystemRenamed;
            watcher.Error -= OnWatchFilesystemError;
            watcher.Dispose();
        }

        _folderWatchers = null;
        IsWatchingFolder = false;
    }

    private void OnWatchFilesystemChanged(object sender, FileSystemEventArgs e)
    {
        QueueWatchReload($"{e.ChangeType}: {Path.GetFileName(e.FullPath)}");
    }

    private void OnWatchFilesystemRenamed(object sender, RenamedEventArgs e)
    {
        QueueWatchReload($"Renamed: {Path.GetFileName(e.OldFullPath)} -> {Path.GetFileName(e.FullPath)}");
    }

    private void OnWatchFilesystemError(object sender, ErrorEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            BottomStatusText = "Watch folder error. Restarting watcher.";
            RestartFolderWatchIfEnabled();
        });
    }

    private void QueueWatchReload(string reason)
    {
        CancellationTokenSource cts;
        lock (_watchReloadGate)
        {
            _watchReloadCts?.Cancel();
            _watchReloadCts?.Dispose();
            _watchReloadCts = new CancellationTokenSource();
            cts = _watchReloadCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (IsAnalyzing || !WatchFolderEnabled)
                    {
                        return;
                    }

                    BottomStatusText = $"Watch: change detected ({reason}). Reloading frame list...";
                    await AnalyzeAsync();
                });
            }
            catch (TaskCanceledException)
            {
                // Intentionally ignored: rapid filesystem events are debounced.
            }
        });
    }

    private void ApplyThresholds()
    {
        ApplyThresholds(updateStatus: true);
    }

    private void ApplyThresholds(bool updateStatus)
    {
        _rejectionService.RevalidateAll(_resultContexts.Select(context => context.Frame), _thresholds, _filterThresholds);

        RebuildResults();
        if (updateStatus)
        {
            var scopeLabel = string.Equals(SelectedRejectionFilter, AllFiltersScopeLabel, StringComparison.OrdinalIgnoreCase)
                ? "all filters"
                : SelectedRejectionFilter;

            StatusText = Results.Count == 0
                ? StatusText
                : $"Applied {scopeLabel} thresholds and revalidated {_resultContexts.Count} frame(s). {Results.Count(result => result.IsRejected)} currently rejected.";
        }

        RaiseThresholdPanelDiagnosticsChanged();
    }

    private void ReapplyThresholdsFromSidebar()
    {
        if (IsAnalyzing || _resultContexts.Count == 0)
        {
            return;
        }

        ApplyThresholds(updateStatus: false);
    }

    private async void StartMoveRejected()
    {
        await MoveRejectedAsync();
    }

    public Task MoveRejectedAsync()
    {
        var moved = _moveService.MoveRejected(_resultContexts.Select(context => context.Frame), RejectedFolder);
        StatusText = moved.Count == 0
            ? "No rejected frames were moved."
            : $"Moved {moved.Count} rejected frame(s) to {RejectedFolder}";
        BottomStatusText = StatusText;
        return Task.CompletedTask;
    }

    private void ToggleReject(FrameSummaryViewModel? summary)
    {
        if (summary is null)
        {
            return;
        }

        var context = _resultContexts.FirstOrDefault(item => string.Equals(item.Frame.FilePath, summary.FilePath, StringComparison.OrdinalIgnoreCase));
        if (context is null)
        {
            return;
        }

        context.Frame.SetManualRejectedOverride(!context.Frame.IsRejected);
        RebuildResults();
    }

    private async void StartLoadSelectedPreview()
    {
        var selected = SelectedResult;
        if (selected is null)
        {
            _selectedPreviewRenderedImage = null;
            SelectedPreviewImage = null;
            SelectedPreviewCaption = "Select a frame to preview it here.";
            BottomStatusText = SelectedPreviewCaption;
            return;
        }

        SelectedPreviewCaption = $"Loading preview for {selected.FileName}...";

        var context = _resultContexts.FirstOrDefault(item => string.Equals(item.Frame.FilePath, selected.FilePath, StringComparison.OrdinalIgnoreCase));
        if (context is null)
        {
            SelectedPreviewCaption = selected.FileName;
            BottomStatusText = SelectedPreviewCaption;
            return;
        }

        if (!File.Exists(context.Frame.FilePath))
        {
            SelectedPreviewCaption = $"{selected.FileName} (source file unavailable)";
            BottomStatusText = SelectedPreviewCaption;
            return;
        }

        try
        {
            var raw = await _analysisService.LoadRawFrameAsync(context.Frame.FilePath, CancellationToken.None);
            var renderFrame = raw;
            if (IsAlignmentEnabled)
            {
                renderFrame = _analysisService.ApplyOrientation(renderFrame, context.Rotate180);
                renderFrame = _analysisService.ApplyShift(renderFrame, context.ShiftX, context.ShiftY);
            }

            StfParameters stf;
            if (_useAutoStretchForPreview)
            {
                stf = _analysisService.ComputeAutoStretch(renderFrame, StfTargetBackground);
                _suppressStfPreviewRefresh = true;
                try
                {
                    StfShadows = stf.Shadows;
                    StfMidtones = stf.Midtones;
                    StfHighlights = stf.Highlights;
                }
                finally
                {
                    _suppressStfPreviewRefresh = false;
                }
            }
            else
            {
                stf = new StfParameters(StfShadows, StfMidtones, StfHighlights);
            }

            var preview = await _analysisService.RenderFullPreviewImageAsync(renderFrame, stf, CancellationToken.None);

            if (!ReferenceEquals(selected, SelectedResult))
            {
                return;
            }

            _selectedPreviewRenderedImage = preview;
            SelectedPreviewImage = preview.ToBitmap();
            SelectedPreviewCaption = selected.FileName;
            _cachedPreviewPaths.Add(selected.FilePath);
            OnPropertyChanged(nameof(CachedPreviewCount));
            UpdatePerformanceText();
            if (!IsAnalyzing)
            {
                BottomStatusText = SelectedPreviewCaption;
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(selected, SelectedResult))
            {
                return;
            }

            SelectedPreviewCaption = $"{selected.FileName} (preview failed: {ex.Message})";
            BottomStatusText = SelectedPreviewCaption;
        }
    }

    public bool SelectPreviousResult()
    {
        return MoveSelectedResult(-1);
    }

    private void UpdatePerformanceText(double? framesPerSecond = null)
    {
        if (_lastAnalysisElapsed is not TimeSpan elapsed)
        {
            PerformanceText = $"Idle | Cached previews: {CachedPreviewCount}";
            return;
        }

        var rate = framesPerSecond ?? (elapsed.TotalSeconds <= 0.001
            ? _lastAnalysisFrameCount
            : _lastAnalysisFrameCount / elapsed.TotalSeconds);
        PerformanceText = $"Analyze: {elapsed.TotalSeconds:F1}s | Frames/s: {rate:F2} | Read: {_lastAnalysisReadGibPerSecond:F2} GB/s | Cached: {CachedPreviewCount}";
    }

    public void ApplyAutoStretchToSelectedPreview()
    {
        _useAutoStretchForPreview = true;
        if (SelectedResult is not null)
        {
            StartLoadSelectedPreview();
        }
    }

    public bool SelectNextResult()
    {
        return MoveSelectedResult(1);
    }

    public bool ToggleSelectedReject()
    {
        if (SelectedResult is null)
        {
            return false;
        }

        ToggleReject(SelectedResult);
        return true;
    }

    public bool SelectResultAtIndex(int index)
    {
        if (Results.Count == 0)
        {
            return false;
        }

        var clamped = Math.Clamp(index, 0, Results.Count - 1);
        var target = Results[clamped];
        if (ReferenceEquals(target, SelectedResult))
        {
            return false;
        }

        SelectedResult = target;
        return true;
    }

    public bool IsPreviewCached(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return _cachedPreviewPaths.Contains(filePath);
    }

    private bool MoveSelectedResult(int direction)
    {
        if (Results.Count == 0 || direction == 0)
        {
            return false;
        }

        var currentIndex = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        var targetIndex = currentIndex < 0
            ? (direction > 0 ? 0 : Results.Count - 1)
            : Math.Clamp(currentIndex + direction, 0, Results.Count - 1);

        if (targetIndex == currentIndex)
        {
            return false;
        }

        SelectedResult = Results[targetIndex];
        return true;
    }

    private void SetStfAndRefresh(ref double field, double value, double min, double max, [CallerMemberName] string? propertyName = null)
    {
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(field - clamped) < 0.0001)
        {
            return;
        }

        field = clamped;
        OnPropertyChanged(propertyName);

        if (_suppressStfPreviewRefresh)
        {
            return;
        }

        _useAutoStretchForPreview = false;
        if (SelectedResult is not null)
        {
            StartLoadSelectedPreview();
        }
    }

    private void RebuildResults()
    {
        var selectedPath = SelectedResult?.FilePath;
        var scopedContexts = GetFilterScopedContexts().ToList();
        var scoreMap = ComputeScores(_resultContexts);
        foreach (var context in _resultContexts)
        {
            if (scoreMap.TryGetValue(context.Frame.FilePath, out var score))
            {
                context.Frame.OverallScore = score;
            }
        }
        var indicatorMap = ComputeIndicatorColors(scopedContexts);

        Results.Clear();
        foreach (var context in GetVisibleContexts())
        {
            Results.Add(CreateFrameSummary(context, scoreMap, indicatorMap));
        }

        SelectedResult = Results.FirstOrDefault(result => string.Equals(result.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? Results.FirstOrDefault();
        OnPropertyChanged(nameof(ResultCountText));
        OnPropertyChanged(nameof(SelectedDecisionText));
        OnPropertyChanged(nameof(TotalFrameCount));
        OnPropertyChanged(nameof(ApprovedFrameCount));
        OnPropertyChanged(nameof(RejectedFrameCount));
        OnPropertyChanged(nameof(OverallAcceptedRatio));
        OnPropertyChanged(nameof(ApprovedFramePercentageText));
        OnPropertyChanged(nameof(RejectedFramePercentageText));
        OnPropertyChanged(nameof(AcceptedIntegrationTimeText));
        OnPropertyChanged(nameof(TotalIntegrationTimeText));
        OnPropertyChanged(nameof(FilterSummaries));
        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasMultipleFilterChips));
        OnPropertyChanged(nameof(PreviewFrameSliderMaximum));
        OnPropertyChanged(nameof(PreviewFramePositionText));
        _applyThresholdsCommand.RaiseCanExecuteChanged();
        _moveRejectedCommand.RaiseCanExecuteChanged();
        _resetThresholdsCommand.RaiseCanExecuteChanged();
        _markSelectedKeepCommand.RaiseCanExecuteChanged();
        _markSelectedRejectedCommand.RaiseCanExecuteChanged();
        _clearSelectedOverrideCommand.RaiseCanExecuteChanged();
        RaiseThresholdPanelDiagnosticsChanged();
    }

    private void SyncPreviewSliderFromSelection()
    {
        if (Results.Count == 0)
        {
            _isSynchronizingPreviewSlider = true;
            PreviewFrameSliderValue = 0;
            _isSynchronizingPreviewSlider = false;
            return;
        }

        var index = SelectedResult is null ? 0 : Math.Max(0, Results.IndexOf(SelectedResult));
        _isSynchronizingPreviewSlider = true;
        PreviewFrameSliderValue = index;
        _isSynchronizingPreviewSlider = false;
    }

    private void RefreshFilterChips()
    {
        var previous = FilterChips.ToDictionary(chip => chip.Key, chip => chip.IsSelected, StringComparer.OrdinalIgnoreCase);
        FilterChips.Clear();

        static int FilterOrder(string key)
        {
            return key.ToUpperInvariant() switch
            {
                "L" => 0,
                "R" => 1,
                "G" => 2,
                "B" => 3,
                "H" or "HA" => 4,
                "O" or "OIII" => 5,
                "S" or "SII" => 6,
                _ => 100,
            };
        }

        var keys = _resultContexts
            .Select(context => NormalizeFilterKey(context.Frame.FilterName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FilterOrder)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in keys)
        {
            var isSelected = !previous.TryGetValue(key, out var previousValue) || previousValue;
            FilterChips.Add(new FilterChipViewModel(key, isSelected, RebuildResults));
        }

        if (!RejectionFilterOptions.Contains(_selectedRejectionFilter, StringComparer.OrdinalIgnoreCase))
        {
            _selectedRejectionFilter = AllFiltersScopeLabel;
            OnPropertyChanged(nameof(SelectedRejectionFilter));
        }

        OnPropertyChanged(nameof(HasFilterChips));
        OnPropertyChanged(nameof(HasMultipleFilterChips));
        OnPropertyChanged(nameof(RejectionFilterOptions));
        RaiseThresholdPanelDiagnosticsChanged();
    }

    private IEnumerable<FrameResultContext> GetThresholdScopeContexts()
    {
        if (string.Equals(SelectedRejectionFilter, AllFiltersScopeLabel, StringComparison.OrdinalIgnoreCase))
        {
            return _resultContexts;
        }

        var selectedKey = NormalizeFilterKey(SelectedRejectionFilter);
        return _resultContexts.Where(context =>
            string.Equals(NormalizeFilterKey(context.Frame.FilterName), selectedKey, StringComparison.OrdinalIgnoreCase));
    }

    private FrameStatistics GetFrameStatistics() =>
        FrameStatisticsCalculator.Calculate(GetFilterScopedContexts().Select(context => context.Frame));

    private int CountThresholdRejects(Func<FrameResultContext, bool> predicate)
    {
        return GetThresholdScopeContexts().Count(predicate);
    }

    private void RaiseThresholdPanelDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(SatelliteTrailRejectedFrameCount));
        OnPropertyChanged(nameof(FwhmRejectedFrameCount));
        OnPropertyChanged(nameof(FwhmArcsecRejectedFrameCount));
        OnPropertyChanged(nameof(SqmRejectedFrameCount));
        OnPropertyChanged(nameof(SkyTempRejectedFrameCount));
        OnPropertyChanged(nameof(HfrRejectedFrameCount));
        OnPropertyChanged(nameof(EccentricityRejectedFrameCount));
        OnPropertyChanged(nameof(MeanBackgroundRejectedFrameCount));
        OnPropertyChanged(nameof(StarCountRejectedFrameCount));
        OnPropertyChanged(nameof(ScoreRejectedFrameCount));
    }

    private void ResetThresholds()
    {
        var scoreMap = ComputeScores(_resultContexts);
        foreach (var context in _resultContexts)
        {
            if (scoreMap.TryGetValue(context.Frame.FilePath, out var score))
            {
                context.Frame.OverallScore = score;
            }
        }

        _thresholds = Thresholds.CreatePermissive(_resultContexts.Select(context => context.Frame));
        _filterThresholds.Clear();

        RaiseAllThresholdPropertiesChanged();
        OverrideSelectedProfileThresholdsAndRevalidateAll();
        StatusText = "Set rejection thresholds to the worst loaded frame values and revalidated every frame.";
    }

    private void RaiseAllThresholdPropertiesChanged()
    {
        OnPropertyChanged(nameof(MaxFwhm));
        OnPropertyChanged(nameof(MaxFwhmArcsec));
        OnPropertyChanged(nameof(MinSqm));
        OnPropertyChanged(nameof(MaxSkyTemp));
        OnPropertyChanged(nameof(MaxHfr));
        OnPropertyChanged(nameof(MaxEccentricity));
        OnPropertyChanged(nameof(MaxMeanBackground));
        OnPropertyChanged(nameof(MinStars));
        OnPropertyChanged(nameof(MinSatelliteConfidence));
        OnPropertyChanged(nameof(MinScore));
    }

    private IEnumerable<FrameResultContext> GetFilterScopedContexts()
    {
        if (FilterChips.Count == 0)
        {
            return _resultContexts;
        }

        var selectedKeys = FilterChips.Where(chip => chip.IsSelected).Select(chip => chip.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0)
        {
            return [];
        }

        return _resultContexts.Where(context => selectedKeys.Contains(NormalizeFilterKey(context.Frame.FilterName)));
    }

    private IEnumerable<FrameResultContext> GetVisibleContexts()
    {
        IEnumerable<FrameResultContext> query = GetFilterScopedContexts();

        if (!ShowAccepted)
        {
            query = query.Where(context => context.Frame.IsRejected);
        }

        if (!ShowRejected)
        {
            query = query.Where(context => !context.Frame.IsRejected);
        }

        var rules = (SortRules.Count > 0 ? SortRules : [new SortRuleViewModel(SortField, true, RebuildResults)])
            .Select(rule => new FrameSortRule(rule.Field, rule.IsAscending))
            .ToArray();

        var comparer = new FrameSortComparer(rules);
        return query
            .OrderBy(context => context.Frame, comparer)
            .ThenBy(context => context.Frame.FileName, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeFilterKey(string? filterName)
    {
        return string.IsNullOrWhiteSpace(filterName) ? "(no filter)" : filterName.Trim();
    }

    private FrameSummaryViewModel CreateFrameSummary(
        FrameResultContext context,
        IReadOnlyDictionary<string, double>? scoreMap = null,
        IReadOnlyDictionary<string, FrameIndicatorColors>? indicatorMap = null)
    {
        var score = scoreMap is not null && scoreMap.TryGetValue(context.Frame.FilePath, out var scoreValue) ? scoreValue : context.Frame.OverallScore;
        var indicators = indicatorMap is not null && indicatorMap.TryGetValue(context.Frame.FilePath, out var indicatorValue)
            ? indicatorValue
            : FrameIndicatorColors.Default;
        var rejectionReasons = context.Frame.AutomaticRejected
            ? _rejectionService.GetRejectionReasons(context.Frame, ResolveThresholdsForFrame(context.Frame))
            : [];

        return new FrameSummaryViewModel(
            context.Frame.FilePath,
            context.Frame.FileName,
            context.Frame.ExposureDateTime,
            context.Frame.Metrics.Fwhm,
            context.Frame.Metrics.FwhmArcsec,
            context.Frame.Metrics.Hfr,
            context.Frame.Metrics.StarCount,
            context.Frame.Metrics.Eccentricity,
            context.Frame.Metrics.Sqm,
            context.Frame.Metrics.SkyTemp,
            context.Frame.Metrics.MeanBackground,
            context.Frame.Metrics.Median,
            context.Frame.Metrics.Mad,
            context.Frame.Metrics.Min,
            context.Frame.Metrics.MinCount,
            context.Frame.Metrics.Max,
            context.Frame.Metrics.MaxCount,
            context.Frame.Metrics.SatelliteTrailConfidence,
            context.Frame.IsRejected,
            context.Frame.AutomaticRejected,
            context.Frame.ManualRejectedOverride,
            score,
            indicators,
            rejectionReasons,
                _toggleRejectCommand,
            PreviewPayloadCodec.DecodeToBitmap(context.ThumbnailPayload),
            PreviewPayloadCodec.DecodeToBitmap(context.RoiPayload),
            context.Frame.FilterName,
                    context.Frame.ExposureSeconds,
                    context.Width,
                    context.Height,
                    context.Frame.Metrics.TrailX1,
                    context.Frame.Metrics.TrailY1,
                    context.Frame.Metrics.TrailX2,
                    context.Frame.Metrics.TrailY2,
                    context.Frame.Metrics.Stars,
                    context.OrientationDebug);
    }

    private Dictionary<string, double> ComputeScores(IReadOnlyList<FrameResultContext> contexts)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (contexts.Count == 0)
        {
            return result;
        }

        var fwhmWeight = UseScoreFwhm ? ScoreWeightFwhm : 0.0;
        var eccWeight = UseScoreEccentricity ? ScoreWeightEccentricity : 0.0;
        var trailWeight = UseScoreTrail ? ScoreWeightTrail : 0.0;
        var hfrWeight = UseScoreHfr ? ScoreWeightHfr : 0.0;
        var starsWeight = UseScoreStars ? ScoreWeightStars : 0.0;
        var bgWeight = UseScoreBackground ? ScoreWeightBackground : 0.0;
        var totalWeight = fwhmWeight + eccWeight + trailWeight + hfrWeight + starsWeight + bgWeight;
        if (totalWeight <= 0)
        {
            totalWeight = 1.0;
        }

        foreach (var group in contexts.GroupBy(context => NormalizeFilterKey(context.Frame.FilterName), StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            var fwhmPct = RankPercentile(members.Select(member => member.Frame.Metrics.Fwhm).ToArray(), lowerIsBetter: true);
            var eccPct = RankPercentile(members.Select(member => member.Frame.Metrics.Eccentricity).ToArray(), lowerIsBetter: true);
            var hfrPct = RankPercentile(members.Select(member => member.Frame.Metrics.Hfr).ToArray(), lowerIsBetter: true);
            var starsPct = RankPercentile(members.Select(member => (double)member.Frame.Metrics.StarCount).ToArray(), lowerIsBetter: false);
            var bgPct = RankPercentile(members.Select(member => member.Frame.Metrics.MeanBackground).ToArray(), lowerIsBetter: true);
            var trailPct = RankPercentile(members.Select(member => (double)member.Frame.Metrics.SatelliteTrailConfidence).ToArray(), lowerIsBetter: true);

            for (var index = 0; index < members.Length; index++)
            {
                var weighted = fwhmPct[index] * fwhmWeight
                             + eccPct[index] * eccWeight
                             + hfrPct[index] * hfrWeight
                             + starsPct[index] * starsWeight
                             + bgPct[index] * bgWeight
                             + trailPct[index] * trailWeight;
                result[members[index].Frame.FilePath] = Math.Clamp((weighted / totalWeight) * 5.0, 0.0, 5.0);
            }
        }

        return result;
    }

    private static Dictionary<string, FrameIndicatorColors> ComputeIndicatorColors(IReadOnlyList<FrameResultContext> contexts)
    {
        var result = new Dictionary<string, FrameIndicatorColors>(StringComparer.OrdinalIgnoreCase);
        if (contexts.Count == 0)
        {
            return result;
        }

        var avgFwhm = contexts.Average(context => context.Frame.Metrics.Fwhm);
        var avgHfr = contexts.Average(context => context.Frame.Metrics.Hfr);
        var avgStars = contexts.Average(context => context.Frame.Metrics.StarCount);
        var avgEcc = contexts.Average(context => context.Frame.Metrics.Eccentricity);
        var avgBg = contexts.Average(context => context.Frame.Metrics.MeanBackground);

        foreach (var context in contexts)
        {
            result[context.Frame.FilePath] = new FrameIndicatorColors(
                CompareLowerIsBetter(context.Frame.Metrics.Fwhm, avgFwhm),
                CompareLowerIsBetter(context.Frame.Metrics.Hfr, avgHfr),
                CompareHigherIsBetter(context.Frame.Metrics.StarCount, avgStars),
                CompareLowerIsBetter(context.Frame.Metrics.Eccentricity, avgEcc),
                CompareLowerIsBetter(context.Frame.Metrics.MeanBackground, avgBg),
                context.Frame.Metrics.SatelliteTrailConfidence >= 60 ? ColorRed : ColorGreen,
                GetFilterBorderColor(context.Frame.FilterName));
        }

        return result;
    }

    private static string CompareLowerIsBetter(double value, double average)
    {
        if (average <= 0)
        {
            return ColorYellow;
        }

        if (value <= average * 0.92)
        {
            return ColorGreen;
        }

        if (value >= average * 1.08)
        {
            return ColorRed;
        }

        return ColorYellow;
    }

    private static string CompareHigherIsBetter(double value, double average)
    {
        if (average <= 0)
        {
            return ColorYellow;
        }

        if (value >= average * 1.08)
        {
            return ColorGreen;
        }

        if (value <= average * 0.92)
        {
            return ColorRed;
        }

        return ColorYellow;
    }

    private static double[] RankPercentile(double[] values, bool lowerIsBetter)
    {
        var count = values.Length;
        if (count == 0)
        {
            return [];
        }

        if (count == 1)
        {
            return [1.0];
        }

        var percentiles = new double[count];
        var indexed = values
            .Select((value, index) => (value, index))
            .Where(item => double.IsFinite(item.value))
            .OrderBy(item => item.value)
            .ToArray();

        if (indexed.Length == 0)
        {
            return percentiles;
        }

        if (indexed.Length == 1)
        {
            percentiles[indexed[0].index] = 1.0;
            return percentiles;
        }

        var rank = 0;
        while (rank < indexed.Length)
        {
            var value = indexed[rank].value;
            var tieEnd = rank;
            while (tieEnd + 1 < indexed.Length && indexed[tieEnd + 1].value == value)
            {
                tieEnd++;
            }

            var averageRank = (rank + tieEnd) / 2.0;
            var normalized = averageRank / (indexed.Length - 1.0);
            var percentile = lowerIsBetter ? (1.0 - normalized) : normalized;

            for (var index = rank; index <= tieEnd; index++)
            {
                percentiles[indexed[index].index] = percentile;
            }

            rank = tieEnd + 1;
        }

        return percentiles;
    }

    private static string GetFilterBorderColor(string? filterName)
    {
        var key = NormalizeFilterKey(filterName);
        return key.ToUpperInvariant() switch
        {
            "HA" => "#E07A7A",
            "OIII" => "#6FD8D8",
            "SII" => "#E0C36F",
            "L" => "#DDDDDD",
            "R" => "#FF7070",
            "G" => "#7FE090",
            "B" => "#8FB3FF",
            _ => "#6D88C4",
        };
    }

    private const string ColorGreen = "#5CA36E";
    private const string ColorYellow = "#DAA520";
    private const string ColorRed = "#CD5C5C";

    private static string FormatIntegrationTime(double totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return string.Empty;
        }

        return totalSeconds >= 3600
            ? $"{totalSeconds / 3600.0:F1} h"
            : $"{totalSeconds / 60.0:F0} min";
    }

    private string? ComputeRelativePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(InputFolder))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(InputFolder);
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, directory);
            return string.Equals(relative, ".", StringComparison.Ordinal) ? null : relative;
        }
        catch
        {
            return null;
        }
    }

    private Thresholds CloneThresholds()
    {
        return new Thresholds
        {
            MaxFwhm = _thresholds.MaxFwhm,
            MaxFwhmArcsec = _thresholds.MaxFwhmArcsec,
            MinSqm = _thresholds.MinSqm,
            MaxSkyTemp = _thresholds.MaxSkyTemp,
            MaxHfr = _thresholds.MaxHfr,
            MaxEccentricity = _thresholds.MaxEccentricity,
            MaxMeanBackground = _thresholds.MaxMeanBackground,
            MinStars = _thresholds.MinStars,
            MinSatelliteConfidence = _thresholds.MinSatelliteConfidence,
            MinScore = _thresholds.MinScore,
            AutoCalcTrailThreshold = _thresholds.AutoCalcTrailThreshold,
            AutoCalcFwhmThreshold = _thresholds.AutoCalcFwhmThreshold,
            AutoCalcFwhmArcsecThreshold = _thresholds.AutoCalcFwhmArcsecThreshold,
            AutoCalcSqmThreshold = _thresholds.AutoCalcSqmThreshold,
            AutoCalcSkyTempThreshold = _thresholds.AutoCalcSkyTempThreshold,
            AutoCalcHfrThreshold = _thresholds.AutoCalcHfrThreshold,
            AutoCalcEccentricityThreshold = _thresholds.AutoCalcEccentricityThreshold,
            AutoCalcMeanBackgroundThreshold = _thresholds.AutoCalcMeanBackgroundThreshold,
            AutoCalcStarsThreshold = _thresholds.AutoCalcStarsThreshold,
            AutoCalcScoreThreshold = _thresholds.AutoCalcScoreThreshold,
        };
    }

    private void AddSortRule()
    {
        var remaining = SortFieldOptions.FirstOrDefault(option => !SortRules.Any(rule => string.Equals(rule.Field, option, StringComparison.OrdinalIgnoreCase)));
        SortRules.Add(new SortRuleViewModel(remaining ?? SortFieldOptions[0], true, RebuildResults));
        _addSortRuleCommand.RaiseCanExecuteChanged();
        _removeSortRuleCommand.RaiseCanExecuteChanged();
        RebuildResults();
    }

    private void RemoveSortRule(SortRuleViewModel? rule)
    {
        if (rule is null || SortRules.Count <= 1)
        {
            return;
        }

        SortRules.Remove(rule);
        _addSortRuleCommand.RaiseCanExecuteChanged();
        _removeSortRuleCommand.RaiseCanExecuteChanged();
        RebuildResults();
    }

    private bool SetBool(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private bool SetBoolAndRebuild(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        RebuildResults();
        return true;
    }

    private bool SetDouble(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.0001)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private bool SetDoubleAndRebuild(ref double field, double value, [CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.0001)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        RebuildResults();
        return true;
    }

    private void SetThresholdValue(
        Func<Thresholds, double> selector,
        Action<Thresholds, double> setter,
        Action<Thresholds, bool> setAutoCalculate,
        double nextValue,
        string propertyName)
    {
        var thresholds = GetSelectedThresholdsForEdit();
        if (Math.Abs(selector(thresholds) - nextValue) < 0.0001)
        {
            return;
        }

        setter(thresholds, nextValue);
        setAutoCalculate(thresholds, false);
        OnPropertyChanged(propertyName);
        PersistSelectedThresholdScopeAndRevalidateAll();
    }

    private static Bitmap CreateDemoPreview()
    {
        const int width = 160;
        const int height = 160;
        var data = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 3;
                var horizontal = (byte)((x / (double)(width - 1)) * 255.0);
                var vertical = (byte)((y / (double)(height - 1)) * 255.0);
                var radial = (byte)(255 - Math.Min(255, (int)(Math.Sqrt(Math.Pow(x - (width / 2.0), 2) + Math.Pow(y - (height / 2.0), 2)) * 3)));
                data[index] = horizontal;
                data[index + 1] = vertical;
                data[index + 2] = radial;
            }
        }

        var image = new RustafitsService.RenderedImage(width, height, data, width * 3);
        return image.ToBitmap();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record FrameSummaryViewModel(
    string FilePath,
    string FileName,
    DateTimeOffset? ExposureDateTime,
    double Fwhm,
    double? FwhmArcsec,
    double Hfr,
    int StarCount,
    double Eccentricity,
    double? Sqm,
    double? SkyTemp,
    double MeanBackground,
    double Median,
    double Mad,
    double Min,
    int MinCount,
    double Max,
    int MaxCount,
    int SatelliteTrailConfidence,
    bool IsRejected,
    bool AutomaticRejected,
    bool? ManualRejectedOverride,
    double OverallScore,
    FrameIndicatorColors Indicators,
    IReadOnlyList<string> RejectionReasons,
    ICommand ToggleRejectCommand,
    Bitmap? Thumbnail,
    Bitmap? RoiImage,
    string? FilterName,
    double? ExposureSeconds,
    int FrameWidth,
    int FrameHeight,
    double? TrailX1,
    double? TrailY1,
    double? TrailX2,
    double? TrailY2,
    IReadOnlyList<MeasuredStar> Stars,
    OrientationDebugInfo? OrientationDebug)
{
    public string MetricsText => $"FWHM {Fwhm:F2} px   HFR {Hfr:F2}   Stars {StarCount}   Ecc {Eccentricity:F3}";

    public string TimestampDisplay => ExposureDateTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a";

    public string ExposureDisplay => ExposureSeconds is null ? "n/a" : $"{ExposureSeconds:F1}s";

    public string FilterDisplay => string.IsNullOrWhiteSpace(FilterName) ? "n/a" : FilterName;

    public string FwhmPixelDisplay => Fwhm.ToString("F2");

    public string FwhmArcsecDisplay => FwhmArcsec is > 0 ? $"{FwhmArcsec:F2}\"" : "n/a";

    public string HfrDisplay => Hfr.ToString("F2");

    public string EccentricityDisplay => Eccentricity.ToString("F3");

    public string SqmDisplay => Sqm is double sqm ? $"{sqm:F3}" : "n/a";

    public string SkyTempDisplay => SkyTemp is double skyTemp ? $"{skyTemp:F1}°" : "n/a";

    public string MeanBackgroundDisplay => MeanBackground.ToString("F1");

    public string MedianDisplay => Median.ToString("F1");

    public string MadDisplay => Mad.ToString("F1");

    public string MinDisplay => $"{Min:F0} / {MinCount}";

    public string MaxDisplay => $"{Max:F0} / {MaxCount}";

    public string TrailText => SatelliteTrailConfidence > 0 ? $"{SatelliteTrailConfidence}%" : "–";

    public string VerdictText => IsRejected ? "Rejected" : "Keep";

    public string RejectionStateLabel
    {
        get
        {
            var prefix = ManualRejectedOverride.HasValue ? "✋ " : string.Empty;
            return IsRejected ? $"{prefix}Rejected" : $"{prefix}Keep";
        }
    }
    public string RejectionStateColor => IsRejected ? "#CD5C5C" : "#5D9A65";

    public bool HasRejectionReasons => RejectionReasons.Count > 0;

    public string ScoreValueText => OverallScore.ToString("F1");

        public string QualityLabel => OverallScore switch
    {
        >= 4.0 => "GOOD",
        >= 2.0 => "FAIR",
        _ => "POOR",
    };

    public string QualityColor => OverallScore switch
    {
        >= 4.0 => "LimeGreen",
        >= 2.0 => "Goldenrod",
        _ => "IndianRed",
    };

    public string QualityBackgroundColor => OverallScore switch
    {
        >= 4.0 => "#3332CD32",
        >= 2.0 => "#33DAA520",
        _ => "#33CD5C5C",
    };

    public double ScoreProgressPercent => Math.Clamp((OverallScore / 5.0) * 100.0, 0.0, 100.0);

    public string FwhmIndicatorColor => Indicators.Fwhm;

    public string HfrIndicatorColor => Indicators.Hfr;

    public string StarsIndicatorColor => Indicators.Stars;

    public string EccentricityIndicatorColor => Indicators.Eccentricity;

    public string MeanBackgroundIndicatorColor => Indicators.MeanBackground;

    public string TrailIndicatorColor => Indicators.Trail;

    public string FilterIndicatorColor => Indicators.Filter;

    public string FilterText => string.IsNullOrWhiteSpace(FilterName) ? "Filter n/a" : $"Filter {FilterName}";

    public string ExposureText => ExposureSeconds is double seconds ? $"Exposure {seconds:F1}s" : "Exposure n/a";

    public bool HasTrailLine => TrailX1 is not null && TrailY1 is not null && TrailX2 is not null && TrailY2 is not null;
}

public sealed record FrameIndicatorColors(
    string Fwhm,
    string Hfr,
    string Stars,
    string Eccentricity,
    string MeanBackground,
    string Trail,
    string Filter)
{
    public static readonly FrameIndicatorColors Default = new("#DAA520", "#DAA520", "#DAA520", "#DAA520", "#DAA520", "#DAA520", "#6D88C4");
}

public sealed class FilterChipViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private readonly Action _onChanged;

    public FilterChipViewModel(string key, bool isSelected, Action onChanged)
    {
        Key = key;
        DisplayName = key;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _onChanged();
        }
    }
}

public sealed record FilterSummaryViewModel(
    string FilterName,
    int Accepted,
    int Rejected,
    int Total,
    string IntegrationTimeText)
{
    public double AcceptedRatio => Total <= 0 ? 0.0 : Accepted / (double)Total;
}

public sealed class SortRuleViewModel : INotifyPropertyChanged
{
    private string _field;
    private bool _isAscending;
    private readonly Action _onChanged;

    public SortRuleViewModel(string field, bool isAscending, Action onChanged)
    {
        _field = field;
        _isAscending = isAscending;
        _onChanged = onChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Field
    {
        get => _field;
        set
        {
            if (_field == value)
            {
                return;
            }

            _field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Field)));
            _onChanged();
        }
    }

    public bool IsAscending
    {
        get => _isAscending;
        set
        {
            if (_isAscending == value)
            {
                return;
            }

            _isAscending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAscending)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectionLabel)));
            _onChanged();
        }
    }

    public string DirectionLabel => IsAscending ? "Ascending" : "Descending";
}

internal sealed record FrameResultContext(
    ProcessedFrame Frame,
    int Width,
    int Height,
    double NormalizationMax,
    string? ThumbnailPayload,
    string? RoiPayload,
    OrientationDebugInfo? OrientationDebug = null,
    bool Rotate180 = false,
    int ShiftX = 0,
    int ShiftY = 0);