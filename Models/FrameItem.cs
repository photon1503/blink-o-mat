using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace blink_o_mat.Models;

public sealed class FrameItem : INotifyPropertyChanged
{
    private bool _isRejected;
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

    public bool IsRejected
    {
        get => _isRejected;
        set
        {
            if (_isRejected == value)
            {
                return;
            }

            _isRejected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
