using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using blink_o_mat.Services;
using blink_o_mat.ViewModels;

namespace blink_o_mat
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            SourceInitialized += (_, _) => WindowTitleBarStyler.Apply(this);
            WindowPlacementService.RestoreMainWindow(this);
            Closing += MainWindow_Closing;
            Title = BuildTitle();
        }

        private static string BuildTitle()
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            // Version 1.0.0.0 is the default (unstamped dev build) — omit it.
            if (v is null || v == new Version(1, 0, 0, 0))
                return "Rejector";
            return $"Rejector {v.Major}.{v.Minor}.{v.Build}";
        }

        private void RejectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;

            var filterCounts = vm.GetRejectedCountByFilter();

            var dialog = new RejectConfirmDialog(vm.RejectedFrameCount, vm.RejectedFolder, filterCounts)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                vm.ExecuteMoveRejected(dialog.SelectedFilterKeys);
            }
        }

        private void FramesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FramesListView.SelectedItem is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() => FramesListView.ScrollIntoView(FramesListView.SelectedItem));
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            WindowPlacementService.SaveMainWindow(this);
            if (DataContext is MainViewModel vm)
                vm.Performance.Dispose();
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var hasCtrlAlt = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            hasCtrlAlt = hasCtrlAlt && (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt));

            if (key == Key.W && hasCtrlAlt)
            {
                if (DataContext is MainViewModel vm && vm.DebugShowUpdateBannerCommand.CanExecute(null))
                {
                    vm.DebugShowUpdateBannerCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }
}
