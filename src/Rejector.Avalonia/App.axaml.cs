using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Rejector.Avalonia.ViewModels;
using Rejector.Avalonia.Views;

namespace Rejector.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;

            var parityFolder = Environment.GetEnvironmentVariable("REJECTOR_CURVATURE_PARITY_FOLDER");
            if (!string.IsNullOrWhiteSpace(parityFolder))
            {
                mainWindow.Opened += async (_, _) =>
                {
                    viewModel.SetInputFolder(parityFolder);
                    await viewModel.AnalyzeAsync();
                    viewModel.IsCurvatureViewVisible = true;
                    new FramePreviewWindow { DataContext = viewModel }.Show(mainWindow);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}