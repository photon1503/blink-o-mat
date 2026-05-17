using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace blink_o_mat.Models;

public sealed class FrameItem : INotifyPropertyChanged
{
    private bool _isRejected;
    private string _thumbnailPath = string.Empty;
    private string _roiThumbnailPath = string.Empty;
    private string _fullPreviewPath = string.Empty;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required AstroMetrics Metrics { get; init; }

    public required string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value)
            {
                return;
            }

            _thumbnailPath = value;
            OnPropertyChanged();
        }
    }

    public required string RoiThumbnailPath
    {
        get => _roiThumbnailPath;
        set
        {
            if (_roiThumbnailPath == value)
            {
                return;
            }

            _roiThumbnailPath = value;
            OnPropertyChanged();
        }
    }

    public required string FullPreviewPath
    {
        get => _fullPreviewPath;
        set
        {
            if (_fullPreviewPath == value)
            {
                return;
            }

            _fullPreviewPath = value;
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
