using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Rejector.Avalonia.ViewModels;
using Rejector.Avalonia.Views;
using System.Text.Json;
using System.IO;
using Avalonia.Controls.ApplicationLifetimes;

namespace Rejector.Avalonia.Views;

public partial class MainWindow : Window
{
    private FramePreviewWindow? _previewWindow;
    private static readonly FuncControlTemplate<ToggleButton> ChipTemplate = new((control, _) =>
        new Border
        {
            [!Border.BackgroundProperty] = control[!TemplatedControl.BackgroundProperty],
            [!Border.BorderBrushProperty] = control[!TemplatedControl.BorderBrushProperty],
            [!Border.BorderThicknessProperty] = control[!TemplatedControl.BorderThicknessProperty],
            [!Border.CornerRadiusProperty] = control[!TemplatedControl.CornerRadiusProperty],
            [!Border.PaddingProperty] = control[!TemplatedControl.PaddingProperty],
            Child = new ContentPresenter
            {
                [!ContentPresenter.ContentProperty] = control[!ContentControl.ContentProperty],
                [!ContentPresenter.HorizontalContentAlignmentProperty] = control[!ContentControl.HorizontalContentAlignmentProperty],
                [!ContentPresenter.VerticalContentAlignmentProperty] = control[!ContentControl.VerticalContentAlignmentProperty],
            },
        });

    private const string WindowPlacementDirectoryName = "Rejector";
    private const string WindowPlacementFileName = "window-placement.json";
    private static readonly string WindowPlacementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        WindowPlacementDirectoryName,
        WindowPlacementFileName);

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();
        PositionChanged += (_, _) => SaveWindowPlacement();
        SizeChanged += (_, _) => SaveWindowPlacement();
        Closed += (_, _) => SaveWindowPlacement();
    }

    private void RestoreWindowPlacement()
    {
        if (!File.Exists(WindowPlacementPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(WindowPlacementPath);
            var settings = JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(json);
            if (settings is null || !settings.TryGetValue("MainWindow", out var placement))
            {
                return;
            }

            if (placement.Width <= 0 || placement.Height <= 0)
            {
                return;
            }

            var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            if (!IsOnScreen(bounds))
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)Math.Round(placement.Left), (int)Math.Round(placement.Top));
            Width = placement.Width;
            Height = placement.Height;
            WindowState = placement.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        catch
        {
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var placement = new WindowPlacement
            {
                Left = Position.X,
                Top = Position.Y,
                Width = Width,
                Height = Height,
                WindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal,
            };

            var settings = File.Exists(WindowPlacementPath)
                ? JsonSerializer.Deserialize<Dictionary<string, WindowPlacement>>(File.ReadAllText(WindowPlacementPath)) ?? new Dictionary<string, WindowPlacement>()
                : new Dictionary<string, WindowPlacement>();

            settings["MainWindow"] = placement;
            Directory.CreateDirectory(Path.GetDirectoryName(WindowPlacementPath)!);
            File.WriteAllText(WindowPlacementPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private bool IsOnScreen(Rect bounds)
    {
        var screenCount = Screens.ScreenCount;
        if (screenCount == 0)
        {
            return true;
        }

        foreach (var screen in Screens.All)
        {
            var placementRect = new PixelRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height));

            if (screen.Bounds.Intersects(placementRect))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public WindowState WindowState { get; set; }
    }

    private static void ApplyChipColors(ToggleButton toggle)
    {
        if (!ReferenceEquals(toggle.Template, ChipTemplate))
        {
            toggle.Template = ChipTemplate;
        }

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
            case "filter":
                var category = toggle.DataContext is FilterChipViewModel filterChip
                    ? filterChip.Category
                    : Rejector.Core.Services.FilterCategory.Unknown;
                var (checkedBackground, checkedBorder, checkedForeground) = Rejector.Core.Services.FilterClassifier.GetColors(category);
                background = isChecked ? checkedBackground : "#0A0A0A";
                border = isChecked ? checkedBorder : "#222222";
                foreground = isChecked ? checkedForeground : "#55777777";
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

    private void FilterChip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel && sender is ToggleButton { DataContext: FilterChipViewModel chip } toggle)
        {
            viewModel.ToggleFilterExclusively(chip);
            ApplyChipColors(toggle);
            e.Handled = true;
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

    private async void RejectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var filterCounts = viewModel.GetRejectedCountsByFilter();
        if (filterCounts.Count == 0)
        {
            return;
        }

        var dialog = new RejectConfirmWindow(viewModel.RejectedFrameCount, viewModel.RejectedFolder, filterCounts);
        var result = await dialog.ShowDialog<RejectConfirmWindow?>(this);
        if (result is not null)
        {
            await viewModel.MoveRejectedAsync(result.SelectedFilterKeys);
        }
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