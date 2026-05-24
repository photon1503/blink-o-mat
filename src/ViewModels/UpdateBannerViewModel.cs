using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using blink_o_mat.Infrastructure;
using blink_o_mat.Services;
using blink_o_mat.Views;

namespace blink_o_mat.ViewModels;

public sealed class UpdateBannerViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _latestVersion = string.Empty;
    private string _releaseNotesMarkdown = string.Empty;
    private string? _installerUrl;
    private bool _isDownloading;
    private string _statusMessage = string.Empty;

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

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateButtonText));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string Message =>
        $"Version {LatestVersion} is available — click to download and install now.";

    public string UpdateButtonText => IsDownloading ? "Downloading…" : "Download & Install";

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(_releaseNotesMarkdown);

    public ICommand DismissCommand { get; }
    public ICommand DownloadAndUpdateCommand { get; }
    public ICommand ShowReleaseNotesCommand { get; }

    public UpdateBannerViewModel()
    {
        DismissCommand = new RelayCommand(_ => IsVisible = false);
        DownloadAndUpdateCommand = new RelayCommand(
            _ => _ = DownloadAndUpdateAsync(),
            _ => !IsDownloading);
        ShowReleaseNotesCommand = new RelayCommand(
            _ => ShowReleaseNotes(),
            _ => HasReleaseNotes);
    }

    private void ShowReleaseNotes()
    {
        var window = new ReleaseNotesWindow(LatestVersion, _releaseNotesMarkdown)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    private async Task DownloadAndUpdateAsync()
    {
        IsDownloading = true;
        StatusMessage = "Fetching installer URL…";
        try
        {
            var downloadUrl = _installerUrl;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                var service = new UpdateCheckService();
                downloadUrl = await service.GetInstallerDownloadUrlAsync();
            }

            if (string.IsNullOrEmpty(downloadUrl))
            {
                // Fall back to opening the releases page
                StatusMessage = string.Empty;
                Process.Start(new ProcessStartInfo(UpdateCheckService.ReleasesPageUrl)
                    { UseShellExecute = true });
                return;
            }

            StatusMessage = "Downloading installer…";
            var tempPath = Path.Combine(Path.GetTempPath(), $"Rejector-Setup-{LatestVersion}.exe");

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Rejector-UpdateCheck/1.0");
                var bytes = await http.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);
            }

            StatusMessage = "Launching installer…";
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
            // Shut down the current instance so the installer can replace the files
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public void ShowUpdate(string latestVersion)
    {
        LatestVersion = latestVersion;
        IsVisible = true;
    }

    public void ShowUpdate(UpdateInfo info)
    {
        _releaseNotesMarkdown = info.ReleaseNotesMarkdown ?? string.Empty;
        _installerUrl = info.InstallerUrl;
        LatestVersion = info.Version;
        OnPropertyChanged(nameof(HasReleaseNotes));
        IsVisible = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

