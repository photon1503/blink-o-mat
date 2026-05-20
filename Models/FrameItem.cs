using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace blink_o_mat.Models;

public sealed class FrameItem : INotifyPropertyChanged
{
    private bool _autoRejected;
    private bool? _manualRejectedOverride;
    private bool _isPreviewActive;
    private BitmapSource? _thumbnailImage;
    private BitmapSource? _roiImage;
    private WpfBrush _fwhmIndicatorBrush = WpfBrushes.Goldenrod;
    private WpfBrush _hfrIndicatorBrush = WpfBrushes.Goldenrod;
    private WpfBrush _starsIndicatorBrush = WpfBrushes.Goldenrod;
    private WpfBrush _eccentricityIndicatorBrush = WpfBrushes.Goldenrod;
    private WpfBrush _meanBackgroundIndicatorBrush = WpfBrushes.Goldenrod;
    private WpfBrush _trailIndicatorBrush = WpfBrushes.Goldenrod;
    private double _overallScore;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required AstroMetrics Metrics { get; init; }
    public DateTimeOffset? ExposureDateTime { get; init; }
    public double? ExposureSeconds { get; init; }
    public string? FilterName { get; init; }

    public required BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set
        {
            if (ReferenceEquals(_thumbnailImage, value))
            {
                return;
            }

            _thumbnailImage = value;
            OnPropertyChanged();
        }
    }

    public bool IsPreviewActive
    {
        get => _isPreviewActive;
        set
        {
            if (_isPreviewActive == value)
            {
                return;
            }

            _isPreviewActive = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush FwhmIndicatorBrush
    {
        get => _fwhmIndicatorBrush;
        set
        {
            if (Equals(_fwhmIndicatorBrush, value)) return;
            _fwhmIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush HfrIndicatorBrush
    {
        get => _hfrIndicatorBrush;
        set
        {
            if (Equals(_hfrIndicatorBrush, value)) return;
            _hfrIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush StarsIndicatorBrush
    {
        get => _starsIndicatorBrush;
        set
        {
            if (Equals(_starsIndicatorBrush, value)) return;
            _starsIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush EccentricityIndicatorBrush
    {
        get => _eccentricityIndicatorBrush;
        set
        {
            if (Equals(_eccentricityIndicatorBrush, value)) return;
            _eccentricityIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush MeanBackgroundIndicatorBrush
    {
        get => _meanBackgroundIndicatorBrush;
        set
        {
            if (Equals(_meanBackgroundIndicatorBrush, value)) return;
            _meanBackgroundIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public WpfBrush TrailIndicatorBrush
    {
        get => _trailIndicatorBrush;
        set
        {
            if (Equals(_trailIndicatorBrush, value)) return;
            _trailIndicatorBrush = value;
            OnPropertyChanged();
        }
    }

    public double OverallScore
    {
        get => _overallScore;
        set
        {
            if (Math.Abs(_overallScore - value) < 0.001) return;
            _overallScore = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverallScoreStars));
            OnPropertyChanged(nameof(OverallScoreText));
            OnPropertyChanged(nameof(ScoreValueText));
            OnPropertyChanged(nameof(QualityLabel));
            OnPropertyChanged(nameof(QualityBrush));
            OnPropertyChanged(nameof(QualityBackgroundBrush));
            OnPropertyChanged(nameof(ScoreProgressPercent));
        }
    }

    public string OverallScoreStars
    {
        get
        {
            var filled = Math.Clamp((int)Math.Round(_overallScore), 0, 5);
            return new string('★', filled) + new string('☆', 5 - filled);
        }
    }

    public string OverallScoreText => $"{_overallScore:F1}/5";

    public string ScoreValueText => _overallScore.ToString("F1");

    public string QualityLabel => _overallScore switch
    {
        >= 4.0 => "GOOD",
        >= 2.5 => "FAIR",
        _ => "POOR"
    };

    public WpfBrush QualityBrush => _overallScore switch
    {
        >= 4.0 => WpfBrushes.LimeGreen,
        >= 2.5 => WpfBrushes.Goldenrod,
        _ => WpfBrushes.IndianRed
    };

    public WpfBrush QualityBackgroundBrush => _overallScore switch
    {
        >= 4.0 => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x32, 0xCD, 0x32)),
        >= 2.5 => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xDA, 0xA5, 0x20)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xCD, 0x5C, 0x5C))
    };

    public double ScoreProgressPercent => Math.Clamp((_overallScore / 5.0) * 100.0, 0.0, 100.0);

    public string TrailText => Metrics.SatelliteTrailConfidence > 0
        ? $"{Metrics.SatelliteTrailConfidence}%"
        : "–";

    public string FilterDisplay => string.IsNullOrWhiteSpace(FilterName) ? "n/a" : FilterName;

    public string ExposureDisplay => ExposureSeconds is null ? "n/a" : $"{ExposureSeconds:F1}s";

    public string TimestampDisplay => ExposureDateTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a";

    public string FwhmArcsecDisplay => Metrics.FwhmArcsec is > 0 ? $"{Metrics.FwhmArcsec:F2}\"" : "n/a";

    public string SqmDisplay => Metrics.Sqm is double sqm ? $"{sqm:F3}" : "n/a";

    public string SkyTempDisplay => Metrics.SkyTemp is double skyTemp ? $"{skyTemp:F1}°" : "n/a";

    public string MeanBackgroundDisplay => $"{Metrics.MeanBackground:F0} ADU";

    public string MedianDisplay => $"{Metrics.Median:F0} ADU";

    public string MadDisplay => $"{Metrics.Mad:F0} ADU";

    public string MinDisplay => $"{Metrics.Min:F0} ADU ({Metrics.MinCount}x)";

    public string MaxDisplay => $"{Metrics.Max:F0} ADU ({Metrics.MaxCount}x)";

    public bool IsRejected => _manualRejectedOverride ?? _autoRejected;

    public bool AutomaticRejected => _autoRejected;

    public bool IsManualOverrideActive => _manualRejectedOverride.HasValue;

    public bool? ManualRejectedOverride => _manualRejectedOverride;

    public string RejectionStateLabel
    {
        get
        {
            var prefix = IsManualOverrideActive ? "✋ " : string.Empty;
            return IsRejected ? $"{prefix}Rejected" : $"{prefix}Keep";
        }
    }

    public WpfBrush RejectionStateBrush => IsRejected ? WpfBrushes.IndianRed : WpfBrushes.MediumSeaGreen;

    public required BitmapSource? RoiImage
    {
        get => _roiImage;
        set
        {
            if (ReferenceEquals(_roiImage, value))
            {
                return;
            }

            _roiImage = value;
            OnPropertyChanged();
        }
    }

    public void SetAutomaticRejected(bool value)
    {
        if (_autoRejected == value)
        {
            return;
        }

        var previousEffective = IsRejected;
        var previousOverrideActive = IsManualOverrideActive;
        _autoRejected = value;
        NotifyRejectionStateChanges(previousEffective, previousOverrideActive);
    }

    public void SetManualRejectedOverride(bool? value)
    {
        if (_manualRejectedOverride == value)
        {
            return;
        }

        var previousEffective = IsRejected;
        var previousOverrideActive = IsManualOverrideActive;
        _manualRejectedOverride = value;
        NotifyRejectionStateChanges(previousEffective, previousOverrideActive);
    }

    private void NotifyRejectionStateChanges(bool previousEffective, bool previousOverrideActive)
    {
        if (previousEffective != IsRejected)
        {
            OnPropertyChanged(nameof(IsRejected));
            OnPropertyChanged(nameof(RejectionStateBrush));
        }

        if (previousEffective != IsRejected || previousOverrideActive != IsManualOverrideActive)
        {
            OnPropertyChanged(nameof(RejectionStateLabel));
        }

        if (previousEffective != IsRejected || previousOverrideActive != IsManualOverrideActive)
        {
            OnPropertyChanged(nameof(AutomaticRejected));
        }

        if (previousOverrideActive != IsManualOverrideActive)
        {
            OnPropertyChanged(nameof(IsManualOverrideActive));
            OnPropertyChanged(nameof(ManualRejectedOverride));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
