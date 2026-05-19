using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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
    private const int MinimumPreviewCacheAhead = 4;
    private const int MinimumPreviewCacheBehind = 6;
    private const int MaximumPreviewCacheAhead = 24;
    private const int MaximumPreviewCacheBehind = 32;
    private const long PreviewCacheReservedBytes = 256L * 1024 * 1024;
    private const long PreviewCacheHardCapBytes = 1024L * 1024 * 1024;

    private sealed record LoadedFrameContext(
        FrameItem Item,
        float[] Pixels,
        int Width,
        int Height,
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

    private string? _inputFolder;
    private string? _rejectedFolder;
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
    private bool _rejectSatelliteTrail = true;
    private double _stretchStrength = 1.0;
    private bool _useGlobalTargetBackground;
    private double _targetBackground = 0.22;
    private double? _sessionFocalLengthMm;
    private double? _sessionPixelSizeUm;
    private int _approvedFrameCount;
    private int _eccentricityRejectedFrameCount;
    private int _fwhmRejectedFrameCount;
    private int _hfrRejectedFrameCount;
    private int _meanBackgroundRejectedFrameCount;
    private int _rejectedFrameCount;
    private RoiBias _roiBias = RoiBias.Galaxy;
    private int _satelliteTrailRejectedFrameCount;
    private int _sqmRejectedFrameCount;
    private int _skyTempRejectedFrameCount;
    private StretchMode _stretchMode = StretchMode.Default;
    private int _starCountRejectedFrameCount;
    private bool _hasManualRoi;
    private bool _skipRejectedInPreview;
    private FrameItem? _selectedFrame;
    private double _minSqm;
    private double _maxSkyTemp = 40.0;

    private readonly List<LoadedFrameContext> _loadedFrames = [];
    private PreviewWindow? _previewWindow;
    private FramePreviewViewModel? _previewVm;
    private FrameItem? _previewItem;
    private (double X, double Y)? _globalRoiCenter;
    private CancellationTokenSource? _previewCacheCts;
    private CancellationTokenSource? _stretchRefreshCts;
    private readonly SemaphoreSlim _thumbnailRefreshSemaphore = new(1, 1);
    private readonly SemaphoreSlim _previewRefreshSemaphore = new(1, 1);
    private bool _isThumbnailRefreshRunning;
    private bool _thumbnailRefreshPendingWhilePreviewOpen;
    private bool _isInteractiveStretchActive;

    public RangeObservableCollection<FrameItem> Frames { get; } = [];

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

    public int TotalFrameCount => Frames.Count;

    public int RejectedFrameCount
    {
        get => _rejectedFrameCount;
        private set
        {
            if (_rejectedFrameCount == value) return;
            _rejectedFrameCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RejectedFramePercentageText));
        }
    }

    public int ApprovedFrameCount
    {
        get => _approvedFrameCount;
        private set
        {
            if (_approvedFrameCount == value) return;
            _approvedFrameCount = value;
            OnPropertyChanged();
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

    public bool UseGlobalTargetBackground
    {
        get => _useGlobalTargetBackground;
        set
        {
            if (_useGlobalTargetBackground == value) return;
            _useGlobalTargetBackground = value;
            OnPropertyChanged();
            OnStretchSettingsChanged();
        }
    }

    public double TargetBackground
    {
        get => _targetBackground;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.75);
            if (Math.Abs(_targetBackground - clamped) < 0.0001) return;
            _targetBackground = clamped;
            OnPropertyChanged();
            if (UseGlobalTargetBackground)
            {
                OnStretchSettingsChanged();
            }
        }
    }

    private double? ActiveTargetBackground => UseGlobalTargetBackground ? TargetBackground : null;

    public StretchMode StretchMode
    {
        get => _stretchMode;
        set
        {
            if (_stretchMode == value) return;
            _stretchMode = value;
            OnPropertyChanged();
            OnStretchSettingsChanged();
        }
    }

    public Array StretchModeOptions { get; } = Enum.GetValues(typeof(StretchMode));

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

    public double StretchStrength
    {
        get => _stretchStrength;
        set
        {
            var clamped = Math.Clamp(value, 0.25, 5.0);
            if (Math.Abs(_stretchStrength - clamped) < 0.0001) return;
            _stretchStrength = clamped;
            OnPropertyChanged();
            OnStretchSettingsChanged();
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

    public RoiBias RoiBias
    {
        get => _roiBias;
        set
        {
            if (_roiBias == value) return;
            _roiBias = value;
            OnPropertyChanged();
            _globalRoiCenter = null;
            _hasManualRoi = false;
            UpdateAutoRoiCenter();
            ScheduleThumbnailRebuild(immediate: true);
        }
    }

    public Array RoiBiasOptions { get; } = Enum.GetValues(typeof(RoiBias));

    public ICommand BrowseInputCommand { get; }
    public ICommand BrowseRejectedCommand { get; }
    public ICommand LoadFramesCommand { get; }
    public ICommand MoveRejectedCommand { get; }
    public ICommand OpenPreviewCommand { get; }
    public ICommand ToggleRejectCommand { get; }

    public MainViewModel()
    {
        BrowseInputCommand = new RelayCommand(_ => BrowseInput());
        BrowseRejectedCommand = new RelayCommand(_ => BrowseRejected());
        LoadFramesCommand = new RelayCommand(async _ => await LoadFramesAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(InputFolder));
        MoveRejectedCommand = new RelayCommand(_ => MoveRejected(), _ => !IsBusy && Frames.Any(f => f.IsRejected) && !string.IsNullOrWhiteSpace(RejectedFolder));
        OpenPreviewCommand = new RelayCommand(async p => await OpenPreviewAsync(p as FrameItem));
        ToggleRejectCommand = new RelayCommand(p => ToggleFrameReject(p as FrameItem), p => p is FrameItem);

        var settings = _settings.Load();
        InputFolder = settings.InputFolder;
        RejectedFolder = settings.RejectedFolder;
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
            RejectedFolder = RejectedFolder
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
        Frames.Clear();
        _loadedFrames.Clear();
        ResetFrameStatistics();
        SelectedFrame = null;
        _globalRoiCenter = null;
        _hasManualRoi = false;
        SessionFocalLengthMm = null;
        SessionPixelSizeUm = null;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var files = _discovery.Discover(InputFolder);
            ProgressMaximum = Math.Max(1, files.Count);
            var loadedCount = 0;
            var skippedCount = 0;

            if (files.Count == 0)
            {
                Status = "No FITS/XISF frames found.";
                return;
            }

            var firstSuccessfulIndex = -1;
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                Status = $"Loading first frame {i + 1}/{files.Count}: {Path.GetFileName(file)}";

                try
                {
                    var raw = await _rustafits.LoadRawFrameAsync(file, CancellationToken.None);
                    var metrics = _rustafits.AnalyzeFrame(raw);
                    _globalRoiCenter = _rustafits.DetectRoiNormalizedCenter(raw, RoiBias);
                    var previews = await _rustafits.RenderPreviewBitmapsAsync(raw, StretchStrength, StretchMode, ActiveTargetBackground, _globalRoiCenter, metrics, CancellationToken.None);

                    var item = new FrameItem
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        ExposureDateTime = raw.ExposureDateTime,
                        ExposureSeconds = raw.ExposureSeconds,
                        FilterName = raw.FilterName,
                        ThumbnailImage = previews.Full,
                        RoiImage = previews.Roi,
                        Metrics = metrics
                    };

                    Frames.Add(item);
                    _loadedFrames.Add(CreateLoadedFrameContext(item, raw));
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
                var orientationReference = ExpandFrame(_loadedFrames[0]);
                var maxParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);
                using var gate = new SemaphoreSlim(maxParallelism);

                var pending = filesToProcess.Select(async entry =>
                {
                    await gate.WaitAsync(CancellationToken.None);
                    try
                    {
                        var raw = await _rustafits.LoadRawFrameAsync(entry.File, CancellationToken.None);
                        var oriented = _rustafits.NormalizeOrientation(raw, orientationReference);
                        var metrics = _rustafits.AnalyzeFrame(oriented);
                        var previews = await _rustafits.RenderPreviewBitmapsAsync(oriented, StretchStrength, StretchMode, ActiveTargetBackground, _globalRoiCenter, metrics, CancellationToken.None);

                        var item = new FrameItem
                        {
                            FilePath = entry.File,
                            FileName = Path.GetFileName(entry.File),
                            ExposureDateTime = oriented.ExposureDateTime,
                            ExposureSeconds = oriented.ExposureSeconds,
                            FilterName = oriented.FilterName,
                            ThumbnailImage = previews.Full,
                            RoiImage = previews.Roi,
                            Metrics = metrics
                        };

                        return (Item: item, Frame: oriented, Error: (Exception?)null, SourceIndex: entry.SourceIndex, FileName: item.FileName);
                    }
                    catch (Exception ex)
                    {
                        return (Item: (FrameItem?)null, Frame: (RustafitsService.LoadedFrame?)null, Error: ex, SourceIndex: entry.SourceIndex, FileName: Path.GetFileName(entry.File));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                while (pending.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pending);
                    pending.Remove(completedTask);
                    var result = await completedTask;

                    if (result.Item is not null && result.Frame is not null)
                    {
                        Frames.Add(result.Item);
                        _loadedFrames.Add(CreateLoadedFrameContext(result.Item, result.Frame));
                        SessionFocalLengthMm ??= result.Frame.FocalLengthMm;
                        SessionPixelSizeUm ??= result.Frame.PixelSizeUm;
                        loadedCount++;
                        Status = $"Loaded {loadedCount}/{files.Count}: {result.Item.FileName}";
                    }
                    else if (result.Error is not null)
                    {
                        skippedCount++;
                        Status = $"Skipped {result.FileName} ({skippedCount} skipped): {result.Error.Message}";
                    }

                    ProgressValue += 1;
                }
            }

            UpdateFrameComparisons();
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
            frame.TrailIndicatorBrush = frame.Metrics.PossibleSatelliteTrail ? red : green;

            const double fwhmWeight = 2.4;
            const double hfrWeight = 2.2;
            const double starsWeight = 1.1;
            const double eccentricityWeight = 1.2;
            const double backgroundWeight = 0.6;
            const double trailWeight = 1.5;

            var weightedScore = 0.0;
            weightedScore += ScoreLowerIsBetter(frame.Metrics.Fwhm, avgFwhm) * fwhmWeight;
            weightedScore += ScoreLowerIsBetter(frame.Metrics.Hfr, avgHfr) * hfrWeight;
            weightedScore += ScoreHigherIsBetter(frame.Metrics.StarCount, avgStars) * starsWeight;
            weightedScore += ScoreLowerIsBetter(frame.Metrics.Eccentricity, avgEcc) * eccentricityWeight;
            weightedScore += ScoreLowerIsBetter(frame.Metrics.MeanBackground, avgBg) * backgroundWeight;
            weightedScore += (frame.Metrics.PossibleSatelliteTrail ? 0.0 : 1.0) * trailWeight;

            var totalWeight = fwhmWeight + hfrWeight + starsWeight + eccentricityWeight + backgroundWeight + trailWeight;
            frame.OverallScore = Math.Clamp((weightedScore / totalWeight) * 5.0, 0.0, 5.0);
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

    private static double ScoreLowerIsBetter(double value, double average)
    {
        if (average <= 1e-9)
        {
            return 0.5;
        }

        var ratio = value / average;
        return Math.Clamp(1.5 - ratio, 0.0, 1.0);
    }

    private static double ScoreHigherIsBetter(double value, double average)
    {
        if (average <= 1e-9)
        {
            return 0.5;
        }

        var ratio = value / average;
        return Math.Clamp(ratio - 0.5, 0.0, 1.0);
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
            var previewImage = await _rustafits.RenderScaledPreviewBitmapAsync(ExpandFrame(loaded), targetWidth, targetHeight, StretchStrength, StretchMode, ActiveTargetBackground, cancellationToken);
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
            var fullImage = await _rustafits.RenderFullBitmapAsync(ExpandFrame(loaded), StretchStrength, StretchMode, ActiveTargetBackground, cancellationToken);
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

            UpdateAutoRoiCenter();

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

                var frameData = ExpandFrame(loaded);
                var previews = await _rustafits.RenderPreviewBitmapsAsync(frameData, StretchStrength, StretchMode, ActiveTargetBackground, _globalRoiCenter, loaded.Item.Metrics, cancellationToken);

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
            var cacheMiss = _loadedFrames[currentIndex].FullImage is null;
            if (cacheMiss)
            {
                var loadMessage = $"Loading frame {currentIndex + 1}/{_loadedFrames.Count} from disk...";
                _previewVm?.SetPreviewStatus(loadMessage);
                Status = loadMessage;
            }

            _previewItem = item;
            SyncPreviewSelection(item);
            var existingImage = await GetOrCreateFullImageAsync(item);
            _previewVm?.SetItem(item);
            _previewVm?.UpdateFramePosition(currentIndex, _loadedFrames.Count);
            PublishPreviewCacheState();
            _previewWindow.RefreshImage(existingImage);
            StartAdaptivePreviewCaching(item);
            if (cacheMiss)
            {
                _previewVm?.SetPreviewStatus($"Frame {currentIndex + 1}/{_loadedFrames.Count} loaded from disk.");
                Status = $"Frame {currentIndex + 1}/{_loadedFrames.Count} loaded from disk.";
            }
            _previewWindow.Activate();
            await Task.CompletedTask;
            return;
        }

        _previewItem = item;
        SyncPreviewSelection(item);
        var vm = new FramePreviewViewModel(
            item,
            () => StretchStrength,
            value => StretchStrength = value,
            () => RoiBias,
            value => RoiBias = value,
            () => StretchMode,
            value => StretchMode = value,
            () => UseGlobalTargetBackground,
            value => UseGlobalTargetBackground = value,
            () => TargetBackground,
            value => TargetBackground = value,
            BeginInteractiveStretch,
            EndInteractiveStretch,
            SetManualRoi,
            NavigatePreviewAsync,
            NavigatePreviewToIndexAsync,
            TogglePreviewReject,
            () => SkipRejectedInPreview,
            value => SkipRejectedInPreview = value);
        vm.UpdateFramePosition(currentIndex, _loadedFrames.Count);
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

    private void SetManualRoi(WpfPoint point)
    {
        _globalRoiCenter = (
            Math.Clamp(point.X, 0.0, 1.0),
            Math.Clamp(point.Y, 0.0, 1.0));
        _hasManualRoi = true;
        Status = "Manual ROI override set.";
        ScheduleThumbnailRebuild(immediate: true);
    }

    private async Task NavigatePreviewAsync(int direction)
    {
        if (_previewItem is null || direction == 0 || _loadedFrames.Count == 0)
        {
            return;
        }

        var currentIndex = _loadedFrames.FindIndex(f => f.Item == _previewItem);
        if (currentIndex < 0)
        {
            return;
        }

        var nextIndex = currentIndex;
        do
        {
            nextIndex = Math.Clamp(nextIndex + direction, 0, _loadedFrames.Count - 1);
            if (nextIndex == currentIndex)
            {
                return;
            }

            if (!SkipRejectedInPreview || !_loadedFrames[nextIndex].Item.IsRejected)
            {
                break;
            }
        } while (true);

        if (nextIndex == currentIndex)
        {
            return;
        }

        var nextItem = _loadedFrames[nextIndex].Item;
        await OpenPreviewAsync(nextItem);
    }

    private async Task NavigatePreviewToIndexAsync(int index)
    {
        if (_loadedFrames.Count == 0)
        {
            return;
        }

        var targetIndex = Math.Clamp(index, 0, _loadedFrames.Count - 1);
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

        var fullImage = await _rustafits.RenderFullBitmapAsync(ExpandFrame(loaded), StretchStrength, StretchMode, ActiveTargetBackground, CancellationToken.None);
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

        var full = await _rustafits.RenderFullBitmapAsync(ExpandFrame(loaded), StretchStrength, StretchMode, ActiveTargetBackground, cancellationToken);
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
            RejectSatelliteTrail = RejectSatelliteTrail
        };

        foreach (var frame in Frames)
        {
            SetFrameRejected(frame, _rejection.ShouldReject(frame, thresholds), frame.ManualRejectedOverride, refreshStatistics: false);
        }

        UpdateFrameStatistics();
        ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
    }

    private void MoveRejected()
    {
        if (string.IsNullOrWhiteSpace(RejectedFolder))
        {
            return;
        }

        try
        {
            var moved = _move.MoveRejected(Frames, RejectedFolder);
            Status = $"Moved {moved} rejected frame(s).";
        }
        catch (Exception ex)
        {
            Status = $"Move failed: {ex.Message}";
        }
    }

    private void UpdateAutoRoiCenter()
    {
        if (_hasManualRoi || _loadedFrames.Count == 0)
        {
            return;
        }

        _globalRoiCenter = _rustafits.DetectRoiNormalizedCenter(ExpandFrame(_loadedFrames[0]), RoiBias);
    }

    private static LoadedFrameContext CreateLoadedFrameContext(FrameItem item, RustafitsService.LoadedFrame frame)
    {
        return new LoadedFrameContext(
            item,
            (float[])frame.Pixels.Clone(),
            frame.Width,
            frame.Height,
            frame.FocalLengthMm,
            frame.PixelSizeUm,
            frame.ExposureDateTime,
            frame.ExposureSeconds,
            frame.FilterName,
            frame.Sqm,
            frame.SkyTemp,
            null);
    }

    private static RustafitsService.LoadedFrame ExpandFrame(LoadedFrameContext context)
    {
        return new RustafitsService.LoadedFrame(
            (float[])context.Pixels.Clone(),
            context.Width,
            context.Height,
            context.FocalLengthMm,
            context.PixelSizeUm,
            context.ExposureDateTime,
            context.ExposureSeconds,
            context.FilterName,
            context.Sqm,
            context.SkyTemp);
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

        var estimatedFrameBytes = EstimatePreviewFrameBytes(_loadedFrames[centerIndex]);
        var availableCacheBytes = EstimateAvailablePreviewCacheBytes();
        var budgetedFrames = (int)Math.Clamp(availableCacheBytes / Math.Max(1L, estimatedFrameBytes), 1L, 1L + MaximumPreviewCacheAhead + MaximumPreviewCacheBehind);
        var extraFrames = Math.Max(0, budgetedFrames - 1);

        var desiredAhead = Math.Min(MaximumPreviewCacheAhead, MinimumPreviewCacheAhead + (extraFrames / 2));
        var desiredBehind = Math.Min(MaximumPreviewCacheBehind, MinimumPreviewCacheBehind + extraFrames);

        var ahead = Math.Clamp(desiredAhead, Math.Min(MinimumPreviewCacheAhead, maxAheadAvailable), maxAheadAvailable);
        var behind = Math.Clamp(desiredBehind, Math.Min(MinimumPreviewCacheBehind, maxBehindAvailable), maxBehindAvailable);

        if (centerIndex < MinimumPreviewCacheBehind)
        {
            ahead = Math.Min(MaximumPreviewCacheAhead, ahead + (MinimumPreviewCacheBehind - centerIndex));
        }

        if (maxAheadAvailable < MinimumPreviewCacheAhead)
        {
            behind = Math.Min(MaximumPreviewCacheBehind, behind + (MinimumPreviewCacheAhead - maxAheadAvailable));
        }

        ahead = Math.Min(ahead, maxAheadAvailable);
        behind = Math.Min(behind, maxBehindAvailable);

        return (ahead, behind);
    }

    private static long EstimatePreviewFrameBytes(LoadedFrameContext frame)
    {
        var pixelBytes = (long)frame.Width * frame.Height * 4L;
        return Math.Max(8L * 1024 * 1024, pixelBytes + (pixelBytes / 8));
    }

    private static long EstimateAvailablePreviewCacheBytes()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        var totalAvailable = gcInfo.TotalAvailableMemoryBytes;
        if (totalAvailable <= 0)
        {
            return 128L * 1024 * 1024;
        }

        var memoryLoadBytes = totalAvailable * gcInfo.MemoryLoadBytes / Math.Max(1L, gcInfo.HighMemoryLoadThresholdBytes);
        var freeBytes = Math.Max(0L, totalAvailable - memoryLoadBytes - PreviewCacheReservedBytes);
        return Math.Clamp(freeBytes, 64L * 1024 * 1024, PreviewCacheHardCapBytes);
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

        var cachedIndices = new List<int>();
        for (var i = 0; i < _loadedFrames.Count; i++)
        {
            if (_loadedFrames[i].FullImage is not null)
            {
                cachedIndices.Add(i);
            }
        }

        vm.UpdateCachedFrameIndices(cachedIndices);
    }

    private void ResetFrameStatistics()
    {
        OnPropertyChanged(nameof(TotalFrameCount));
        RejectedFrameCount = 0;
        ApprovedFrameCount = 0;
        FwhmRejectedFrameCount = 0;
        HfrRejectedFrameCount = 0;
        SqmRejectedFrameCount = 0;
        SkyTempRejectedFrameCount = 0;
        EccentricityRejectedFrameCount = 0;
        MeanBackgroundRejectedFrameCount = 0;
        StarCountRejectedFrameCount = 0;
        SatelliteTrailRejectedFrameCount = 0;
    }

    private void UpdateFrameStatistics()
    {
        OnPropertyChanged(nameof(TotalFrameCount));
        RejectedFrameCount = Frames.Count(frame => frame.IsRejected);
        ApprovedFrameCount = Math.Max(0, TotalFrameCount - RejectedFrameCount);
        FwhmRejectedFrameCount = Frames.Count(frame => frame.Metrics.Fwhm > MaxFwhm);
        SqmRejectedFrameCount = Frames.Count(frame => frame.Metrics.Sqm.HasValue && frame.Metrics.Sqm.Value < MinSqm);
        SkyTempRejectedFrameCount = Frames.Count(frame => frame.Metrics.SkyTemp.HasValue && frame.Metrics.SkyTemp.Value > MaxSkyTemp);
        HfrRejectedFrameCount = Frames.Count(frame => frame.Metrics.Hfr > MaxHfr);
        EccentricityRejectedFrameCount = Frames.Count(frame => frame.Metrics.Eccentricity > MaxEccentricity);
        MeanBackgroundRejectedFrameCount = Frames.Count(frame => frame.Metrics.MeanBackground > MaxMeanBackground);
        StarCountRejectedFrameCount = Frames.Count(frame => frame.Metrics.StarCount < MinStars);
        SatelliteTrailRejectedFrameCount = RejectSatelliteTrail
            ? Frames.Count(frame => frame.Metrics.PossibleSatelliteTrail)
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
