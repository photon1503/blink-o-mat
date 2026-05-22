using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using blink_o_mat.Infrastructure;
using blink_o_mat.Services;

namespace blink_o_mat.ViewModels;

public sealed class UpdateBannerViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _latestVersion = string.Empty;

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (_latestVersion == value) return;
            _latestVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Message));
        }
    }

    public string Message =>
        $"Version {LatestVersion} is available — click to open the releases page.";

    public ICommand DismissCommand { get; }
    public ICommand OpenReleasesCommand { get; }

    public UpdateBannerViewModel()
    {
        DismissCommand = new RelayCommand(_ => IsVisible = false);
        OpenReleasesCommand = new RelayCommand(_ =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateCheckService.ReleasesPageUrl)
                    { UseShellExecute = true });
            }
            catch { /* ignore */ }
        });
    }

    public void ShowUpdate(string latestVersion)
    {
        LatestVersion = latestVersion;
        IsVisible = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
