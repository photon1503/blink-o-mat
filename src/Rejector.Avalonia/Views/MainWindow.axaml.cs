using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Rejector.Avalonia.ViewModels;

namespace Rejector.Avalonia.Views;

public partial class MainWindow : Window
{
    private FramePreviewWindow? _previewWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    private static void ApplyChipColors(ToggleButton toggle)
    {
        var kind = toggle.Tag as string ?? string.Empty;
        var isChecked = toggle.IsChecked == true;

        string background;
        string border;
        string foreground;

        switch (kind)
        {
            case "accepted":
                background = isChecked ? "#2F3FAE63" : "#161616";
                border = isChecked ? "#7AD68E" : "#444";
                foreground = isChecked ? "#FFE9F8EE" : "#CCBDBDBD";
                break;
            case "rejected":
                background = isChecked ? "#33C45F5F" : "#161616";
                border = isChecked ? "#E07A7A" : "#444";
                foreground = isChecked ? "#FFF9EAEA" : "#CCBDBDBD";
                break;
            default:
                background = isChecked ? "#334F78D1" : "#161616";
                border = isChecked ? "#8FB3FF" : "#444";
                foreground = isChecked ? "#FFE8F0FF" : "#CCBDBDBD";
                break;
        }

        toggle.Background = SolidColorBrush.Parse(background);
        toggle.BorderBrush = SolidColorBrush.Parse(border);
        toggle.Foreground = SolidColorBrush.Parse(foreground);

        if (toggle.Content is TextBlock label)
        {
            label.Foreground = SolidColorBrush.Parse(foreground);
        }
    }

    private void ChipToggle_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            toggle.PropertyChanged -= ChipToggle_PropertyChanged;
            toggle.PropertyChanged += ChipToggle_PropertyChanged;
            ApplyChipColors(toggle);
        }
    }

    private void ChipToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            ApplyChipColors(toggle);
        }
    }

    private void ChipToggle_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is ToggleButton toggle && e.Property == ToggleButton.IsCheckedProperty)
        {
            ApplyChipColors(toggle);
        }
    }

    private void ResultRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (sender is not Control row || row.DataContext is not FrameSummaryViewModel summary)
        {
            return;
        }

        if (e.Source is Control sourceControl && sourceControl.GetSelfAndVisualAncestors().OfType<Button>().Any())
        {
            return;
        }

        viewModel.SelectedResult = summary;
        ShowPreviewWindow(viewModel);
    }

    private void ShowPreviewWindow(MainWindowViewModel viewModel)
    {
        if (_previewWindow is null)
        {
            _previewWindow = new FramePreviewWindow
            {
                DataContext = viewModel,
            };

            _previewWindow.Closed += (_, _) =>
            {
                _previewWindow = null;
            };
        }

        if (!_previewWindow.IsVisible)
        {
            _previewWindow.Show(this);
            return;
        }

        _previewWindow.Activate();
    }

    private void OpenFolderPanel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.IsFolderPanelOpen = !viewModel.IsFolderPanelOpen;
    }

    private void CloseFolderPanel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsFolderPanelOpen = false;
        }
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsSettingsOpen = false;
        }
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select folder(s) with FITS/XISF frames",
        });

        var localPaths = folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (localPaths.Count > 0)
        {
            viewModel.SetInputFolder(string.Join(';', localPaths));
        }
    }

    private async void SaveSession_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save session",
            SuggestedFileName = "rejector-session.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"],
                },
            ],
        });

        var localPath = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await viewModel.SaveSessionAsync(localPath);
        }
    }

    private async void LoadSession_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Load session",
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"],
                },
            ],
        });

        var localPath = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await viewModel.LoadSessionAsync(localPath);
        }
    }

    private async void BrowseRejectedFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Choose a rejected-frame folder",
        });

        var localPath = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            viewModel.SetRejectedFolder(localPath);
        }
    }
}