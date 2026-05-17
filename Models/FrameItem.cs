using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace blink_o_mat.Models;

public sealed class FrameItem : INotifyPropertyChanged
{
    private bool _isRejected;
    private BitmapSource? _thumbnailImage;
    private BitmapSource? _roiImage;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required AstroMetrics Metrics { get; init; }

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
