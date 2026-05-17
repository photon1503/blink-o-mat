using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using blink_o_mat.Infrastructure;
using blink_o_mat.Models;
using blink_o_mat.Services;

namespace blink_o_mat.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private sealed record LoadedFrameContext(
        FrameItem Item,
        RustafitsService.LoadedFrame FrameData,
        BitmapSource FullImage);

    private readonly FrameDiscoveryService _discovery = new();
    private readonly RustafitsService _rustafits = new();
    private readonly FrameRejectionService _rejection = new();
    private readonly FrameMoveService _move = new();

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
    private bool _rejectSatelliteTrail = true;
    private double _stretchStrength = 1.0;

    private readonly List<LoadedFrameContext> _loadedFrames = [];
    private PreviewWindow? _previewWindow;
    private FrameItem? _previewItem;
    private (double X, double Y)? _globalRoiCenter;

    public ObservableCollection<FrameItem> Frames { get; } = [];

    public string? InputFolder
    {
        get => _inputFolder;
        set
        {
            if (_inputFolder == value) return;
            _inputFolder = value;
            OnPropertyChanged();
            ((RelayCommand)LoadFramesCommand).RaiseCanExecuteChanged();
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
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
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
            var clamped = Math.Clamp(value, 0.25, 3.0);
            if (Math.Abs(_stretchStrength - clamped) < 0.0001) return;
            _stretchStrength = clamped;
            OnPropertyChanged();
            _ = RebuildThumbnailsAsync();
        }
    }

    public ICommand BrowseInputCommand { get; }
    public ICommand BrowseRejectedCommand { get; }
    public ICommand LoadFramesCommand { get; }
    public ICommand MoveRejectedCommand { get; }
    public ICommand OpenPreviewCommand { get; }

    public MainViewModel()
    {
        BrowseInputCommand = new RelayCommand(_ => BrowseInput());
        BrowseRejectedCommand = new RelayCommand(_ => BrowseRejected());
        LoadFramesCommand = new RelayCommand(async _ => await LoadFramesAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(InputFolder));
        MoveRejectedCommand = new RelayCommand(_ => MoveRejected(), _ => !IsBusy && Frames.Any(f => f.IsRejected) && !string.IsNullOrWhiteSpace(RejectedFolder));
        OpenPreviewCommand = new RelayCommand(async p => await OpenPreviewAsync(p as FrameItem));
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
        _globalRoiCenter = null;

        try
        {
            var files = _discovery.Discover(InputFolder);
            var tempThumbs = Path.Combine(Path.GetTempPath(), "blink-o-mat-thumbs", DateTime.Now.ToString("yyyyMMddHHmmss"));
            ProgressMaximum = Math.Max(1, files.Count);

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                Status = $"Processing {Path.GetFileName(file)} ({i + 1}/{files.Count})";

                try
                {
                    var raw = await _rustafits.LoadRawFrameAsync(file, CancellationToken.None);
                    var metrics = _rustafits.AnalyzeFrame(raw);
                    _globalRoiCenter ??= _rustafits.DetectRoiNormalizedCenter(raw);

                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var _ = baseName;
                    var previews = await _rustafits.RenderPreviewBitmapsAsync(raw, StretchStrength, _globalRoiCenter, CancellationToken.None);
                    var fullImage = await _rustafits.RenderFullBitmapAsync(raw, StretchStrength, CancellationToken.None);

                    var item = new FrameItem
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        ThumbnailImage = previews.Full,
                        RoiImage = previews.Roi,
                        Metrics = metrics
                    };

                    Frames.Add(item);
                    _loadedFrames.Add(new LoadedFrameContext(item, raw, fullImage));
                }
                catch (Exception ex)
                {
                    Status = $"Skipped {Path.GetFileName(file)}: {ex.Message}";
                }

                ProgressValue = i + 1;
            }

            ApplyThresholds();
            Status = $"Loaded {Frames.Count} frame(s).";
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

    private async Task RebuildThumbnailsAsync()
    {
        if (IsBusy || _loadedFrames.Count == 0)
        {
            return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressValue = 0;
        ProgressMaximum = _loadedFrames.Count;

        try
        {
            for (var i = 0; i < _loadedFrames.Count; i++)
            {
                var loaded = _loadedFrames[i];
                Status = $"Applying stretch ({i + 1}/{_loadedFrames.Count})";

                var previews = await _rustafits.RenderPreviewBitmapsAsync(loaded.FrameData, StretchStrength, _globalRoiCenter, CancellationToken.None);
                var fullImage = await _rustafits.RenderFullBitmapAsync(loaded.FrameData, StretchStrength, CancellationToken.None);

                loaded.Item.ThumbnailImage = previews.Full;
                loaded.Item.RoiImage = previews.Roi;

                if (_previewItem == loaded.Item)
                {
                    _previewWindow?.RefreshImage(fullImage);
                }

                _loadedFrames[i] = loaded with { FullImage = fullImage };

                ProgressValue = i + 1;
            }

            Status = "Stretch updated.";
        }
        catch (Exception ex)
        {
            Status = $"Stretch update failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            ((RelayCommand)MoveRejectedCommand).RaiseCanExecuteChanged();
        }
    }

    private async Task OpenPreviewAsync(FrameItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (_previewWindow is not null)
        {
            _previewItem = item;
            var existing = _loadedFrames.FirstOrDefault(f => f.Item == item);
            _previewWindow.RefreshImage(existing.FullImage);
            _previewWindow.Activate();
            await Task.CompletedTask;
            return;
        }

        _previewItem = item;
        var vm = new FramePreviewViewModel(item, () => StretchStrength, value => StretchStrength = value);
        _previewWindow = new PreviewWindow(vm);
        var current = _loadedFrames.FirstOrDefault(f => f.Item == item);
        _previewWindow.RefreshImage(current.FullImage);
        _previewWindow.Closed += (_, _) =>
        {
            _previewWindow = null;
            _previewItem = null;
        };

        _previewWindow.Show();
        await Task.CompletedTask;
    }

    private void ApplyThresholds()
    {
        var thresholds = new Thresholds
        {
            MaxFwhm = MaxFwhm,
            MaxHfr = MaxHfr,
            MaxEccentricity = MaxEccentricity,
            MaxMeanBackground = MaxMeanBackground,
            RejectSatelliteTrail = RejectSatelliteTrail
        };

        foreach (var frame in Frames)
        {
            frame.IsRejected = _rejection.ShouldReject(frame, thresholds);
        }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
